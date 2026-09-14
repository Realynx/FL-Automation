using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Mixer: track names/count, sends, EQ gain, FX slots (load/remove/clone effects).
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // A signature-resolved array does not prove its element or FX-object layouts. Only the
    // selected native profile can supply these offsets; a scalar stride is insufficient.
    private async Task<FlMixerLayout> MixerLayoutAsync(CancellationToken ct)
        => (await GetSymbolStatusAsync(ct).ConfigureAwait(false))?.MixerLayout
            ?? throw new InvalidOperationException("The running FL Studio build has no verified mixer-track layout; mixer struct access is unavailable.");

    /// <summary>Base address of a mixer track's struct, or 0 when unavailable/out of range.</summary>
    private async Task<ulong> MixerTrackStructAsync(int track, FlMixerLayout layout, CancellationToken ct)
    {
        ValidateMixerTrack(track, await GetMixerTrackCountAsync(ct));
        ulong arrayBase = await GPtrAsync("14a7eb0", ct);
        return arrayBase == 0 ? 0 : MixerTrackAddress(arrayBase, track, layout);
    }

    private static ulong MixerTrackAddress(ulong arrayBase, int track, FlMixerLayout layout)
        => checked(arrayBase + (ulong)track * (ulong)layout.TrackStride);

    private static void ValidateMixerTrack(int track, int count)
    {
        if (track < 0 || track > count - 2)
            throw new ArgumentOutOfRangeException(nameof(track), $"Mixer track must be 0..{count - 2} (Master and ordinary inserts); Current and dormant slots are unavailable.");
    }

    /// <summary>" MUTED"/" SOLO" markers from the profile's track state fields.</summary>
    private async Task<string> MixerTrackStateFlagsAsync(ulong trackStruct, FlMixerLayout layout, CancellationToken ct)
    {
        byte enabled = (await PeekAbsAsync(trackStruct + (ulong)layout.EnabledOffset, 1, ct))[0];
        byte solo = (await PeekAbsAsync(trackStruct + (ulong)layout.SoloOffset, 1, ct))[0];
        return (enabled == 0 ? " MUTED" : "") + (solo != 0 ? " SOLO" : "");
    }

    /// <summary>Read a mixer track's mute state (a zero enabled byte means muted).</summary>
    public async Task<bool> GetMixerTrackMutedAsync(int track, CancellationToken ct = default)
    {
        var layout = await MixerLayoutAsync(ct);
        ulong t = await MixerTrackStructAsync(track, layout, ct);
        if (t == 0) throw new InvalidOperationException($"Mixer track {track} not available.");
        return (await PeekAbsAsync(t + (ulong)layout.EnabledOffset, 1, ct))[0] == 0;
    }

    /// <summary>Mute/unmute a mixer track by writing the profile's enabled byte — the model field
    /// FL's own mute op targets (FLmx_SetTrackEnabledCore; its full arg contract isn't decompiled, so
    /// the byte write + routing refresh is the safe path, same pattern as playlist-track mute).</summary>
    public async Task SetMixerTrackMutedAsync(int track, bool muted, CancellationToken ct = default)
    {
        LogOp("SetMixerTrackMuted", $"track={track} muted={muted}");
        var layout = await MixerLayoutAsync(ct);
        ulong t = await MixerTrackStructAsync(track, layout, ct);
        if (t == 0) throw new InvalidOperationException($"Mixer track {track} not available.");
        await PokeAbsAsync(t + (ulong)layout.EnabledOffset, new byte[] { (byte)(muted ? 0 : 1) }, ct);
        await RefreshMixerRoutingAsync(ct);
    }

    private async Task<ulong> MixerRoutingManagerAsync(CancellationToken ct)
    {
        ulong slot = await GPtrAsync("14a99a0", ct);
        return slot == 0 ? 0 : await APtrAsync(slot, ct);
    }

    private async Task RefreshMixerRoutingAsync(CancellationToken ct)
    {
        ulong manager = await MixerRoutingManagerAsync(ct);
        if (manager > 0x10000) await CallAsync("11a5d20", new ulong[] { manager }, ct);
    }

    /// <summary>A track's active sends read from the send table in ONE peek: "sends: ->0 'Master'
    /// (1.0), ->5 'Reverb' (0.5)". This is the read half SetMixerSendAsync never had — without it the
    /// model couldn't see existing routing at all.</summary>
    private async Task<string> DescribeSendsAsync(ulong trackStruct, FlMixerLayout layout, CancellationToken ct)
    {
        int count = await GetMixerTrackCountAsync(ct);
        int tableBytes = MixerSendTableBytes(count, layout);
        byte[] table = await PeekAbsAsync(trackStruct + (ulong)layout.SendTableOffset, tableBytes, ct);
        var sends = new List<string>();
        for (int d = 0; d <= count - 2; d++)
        {
            int record = d * layout.SendStride;
            if (table[record + layout.SendActiveOffset] == 0) continue;
            double level = BitConverter.ToInt32(table, record + layout.SendLevelOffset) / 16000.0;
            string dn = await MixerTrackNameAsync(d, layout, ct);
            sends.Add($"->{d} '{dn}' ({level:0.###})");
        }
        return sends.Count == 0 ? "sends: none" : "sends: " + string.Join(", ", sends);
    }

    private static int MixerSendTableBytes(int count, FlMixerLayout layout)
    {
        long length = (long)count * layout.SendStride;
        if (length > layout.TrackStride - (long)layout.SendTableOffset)
            throw new InvalidOperationException("Mixer send table exceeds the verified track layout.");
        return checked((int)length);
    }

    /// <summary>Native cardinality including Master and the fixed Current pseudo-track. Ordinary indices are 1..count-2.</summary>
    public async Task<int> GetMixerTrackCountAsync(CancellationToken ct = default)
    {
        ulong cntObj = await GPtrAsync("14a9850", ct);   // *(void**)g_pMixerTrackCount
        if (cntObj == 0) throw new InvalidOperationException("Mixer track count is unavailable.");
        int n = await AI32Async(cntObj, ct);
        return n is >= 3 and <= 502 ? n : throw new InvalidOperationException("FL Studio reported an invalid mixer track count.");
    }

    /// <summary>Effective mixer track name: the custom name if set, else FL's default by type
    /// (Master / "Insert &lt;n&gt;" / Current). Symmetric with <see cref="SetMixerSendAsync"/> addressing.</summary>
    public async Task<string> GetMixerTrackNameAsync(int track, CancellationToken ct = default)
        => await MixerTrackNameAsync(track, await MixerLayoutAsync(ct), ct);

    private async Task<string> MixerTrackNameAsync(int track, FlMixerLayout layout, CancellationToken ct)
    {
        ulong trackStruct = await MixerTrackStructAsync(track, layout, ct);
        if (trackStruct == 0) return "";
        return (await ReadMixerTrackInfoAsync(track, trackStruct, layout, ct)).Name;
    }

    /// <summary>Rename a mixer track. Assigns the profile's name field (the same field
    /// <see cref="GetMixerTrackNameAsync"/> reads) via FL's Delphi_UStrAsg, which DEEP-COPIES the const into an
    /// FL-owned heap string that persists (a raw poke of a scratch-const pointer would dangle). Empty name
    /// pokes 0 to clear back to the type default. Avoids the unconfirmed hub setter 0x11C2810 + its cascade.</summary>
    public async Task SetMixerTrackNameAsync(int track, string name, CancellationToken ct = default)
    {
        LogOp("SetMixerTrackName", $"track={track}");
        var layout = await MixerLayoutAsync(ct);
        ulong t = await MixerTrackStructAsync(track, layout, ct);
        if (t == 0) throw new InvalidOperationException($"Mixer track {track} not available.");
        if (string.IsNullOrEmpty(name))
            await PokeAbsAsync(t + (ulong)layout.NameOffset, BitConverter.GetBytes(0UL), ct);
        else
        {
            using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
            ulong str = await MakeOwnedDelphiStringAsync(name, scratch, ct);
            await UStrAsgAsync(t + (ulong)layout.NameOffset, str, ct);
        }
        await RefreshMixerRoutingAsync(ct);
    }

    /// <summary>
    /// Lists mixer tracks that have a CUSTOM name (plus Master) as "index: name", so a caller can
    /// resolve a bus/track NAME to the numeric index every other mixer tool needs. Unnamed inserts
    /// are omitted (they default to "Insert &lt;n&gt;" at track &lt;n&gt;) to keep the list signal-rich.
    /// </summary>
    public async Task<string> ListMixerTracksAsync(CancellationToken ct = default)
    {
        var layout = await MixerLayoutAsync(ct);
        ulong arrayBase = await GPtrAsync("14a7eb0", ct);
        if (arrayBase == 0) return "(mixer not available)";
        int count = await GetMixerTrackCountAsync(ct);

        var named = new List<string>();
        for (int t = 0; t <= count - 2; t++)
        {
            ct.ThrowIfCancellationRequested();
            ulong trackStruct = MixerTrackAddress(arrayBase, t, layout);
            ulong namePtr = await APtrAsync(trackStruct + (ulong)layout.NameOffset, ct);
            string custom = namePtr == 0 ? "" : await ReadDelphiStringAsync(namePtr, ct);
            if (string.IsNullOrEmpty(custom) && t != 0) continue;
            // Current level + MUTED/SOLO ride along on every listed track (they're the ones a mix
            // turn touches), so the model can reason relatively instead of setting blind.
            long vol = await GetMixerVolumeAsync(t, ct);
            int pan = await GetMixerPanAsync(t, ct);
            string state = await MixerTrackStateFlagsAsync(trackStruct, layout, ct);
            named.Add($"{t}: {(string.IsNullOrEmpty(custom) ? "Master" : custom)} vol={vol}"
                + (pan != 0 ? $" pan={pan}" : "") + state);
        }

        int lastInsert = count - 2;
        var sb = new System.Text.StringBuilder();
        sb.Append($"Mixer: {count} tracks (0=Master, 1-{lastInsert}=Inserts; Current is a separate fixed pseudo-track, excluded from SDK operations). ");
        if (named.Count <= 1)
            sb.Append("No custom-named mixer tracks — unnamed inserts are \"Insert <n>\" at mixer track <n>.");
        else
            sb.Append("Named tracks:\n").Append(string.Join("\n", named))
              .Append("\n(Unnamed inserts are \"Insert <n>\" at mixer track <n>.)");
        return sb.ToString();
    }

    // ============================ Mixer sends ============================

    /// <summary>Set a mixer send srcTrack-&gt;dstTrack at level (1.0 ≈ unity). Engine funcs only (no Python ctx).</summary>
    public async Task SetMixerSendAsync(int srcTrack, int dstTrack, double level, CancellationToken ct = default)
    {
        var layout = await MixerLayoutAsync(ct);
        int count = await GetMixerTrackCountAsync(ct);
        ValidateMixerTrack(srcTrack, count);
        ValidateMixerTrack(dstTrack, count);
        MixerSendTableBytes(count, layout);
        double scaledLevel = Math.Round(level * 16000);
        if (!double.IsFinite(scaledLevel) || scaledLevel < 0 || scaledLevel > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(level), "Mixer send level must be finite, nonnegative and fit the native level field.");
        ulong mgr = await MixerRoutingManagerAsync(ct);
        ulong baseArr = await GPtrAsync("14a7eb0", ct);  // *(0x14A7EB0)
        if (mgr == 0 || baseArr == 0) throw new InvalidOperationException("Mixer routing state is unavailable.");
        await CallAsync("11a67f0", new ulong[] { mgr, (uint)srcTrack, (uint)dstTrack, 1, 1 }, ct);  // FLmx_SetRouteActiveCore
        ulong slot = MixerTrackAddress(baseArr, srcTrack, layout) + (ulong)layout.SendTableOffset
            + (ulong)dstTrack * (ulong)layout.SendStride + (ulong)layout.SendLevelOffset;
        await PokeAbsAsync(slot, BitConverter.GetBytes((int)scaledLevel), ct);
        await CallAsync("11a5d20", new ulong[] { mgr }, ct);  // FLmx_RefreshRouting
    }

    // ============================ Mixer EQ gain ============================

    /// <summary>Set a mixer track EQ band gain (band 0=low,1=mid,2=high; value 0..0x40000000, ~0x20000000 = 0 dB).</summary>
    public async Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default)
    {
        await ValidateAddressableMixerTrackAsync(track, ct);
        await DispatchCommandAsync((uint)(((long)track << 22) + 0x70001FD0 + Math.Clamp(band, 0, 2)), unchecked((ulong)(uint)value), FlagSet, ct);
    }

    /// <summary>Load/clear a plugin through the profile's mixer FX slot load method.
    /// mode 0xFFFFFFFD = insert, 0xFFFFFFFE = clear.</summary>
    private async Task LoadIntoMixerSlotAsync(ulong slot, uint mode, string path, FlMixerLayout layout, CancellationToken ct)
    {
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong strPtr = await WriteDelphiStringAsync(path, scratch, ct);
        await CallVtblAsync(slot, (uint)layout.EffectLoadVtableOffset, "Mixer slot load", new ulong[] { slot, mode, strPtr, 0, 1, 1 }, ct);
    }

    // ---- mixer FX slots ----

    private async Task<ulong> MixerSlotObjAsync(int track, int slot, CancellationToken ct)
        => await MixerSlotObjAsync(track, slot, await MixerLayoutAsync(ct), ct);

    private async Task<ulong> MixerSlotObjAsync(int track, int slot, FlMixerLayout layout, CancellationToken ct)
    {
        if (slot < 0 || slot > 9) throw new InvalidOperationException($"FX slot {slot} out of range (0..9).");
        ulong trackStruct = await MixerTrackStructAsync(track, layout, ct);
        if (trackStruct == 0) return 0;
        return await APtrAsync(trackStruct + (ulong)layout.EffectSlotsOffset + (ulong)slot * (ulong)layout.EffectSlotStride, ct);
    }

    /// <summary>Inspects one mixer track end to end: name, current vol/pan, MUTED/SOLO state, loaded
    /// FX slots, and active sends with levels — the full picture a mix decision needs in one call
    /// (the FX-slot list alone left the model blind to levels and routing).</summary>
    public async Task<string> ListMixerEffectsAsync(int track, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var layout = await MixerLayoutAsync(ct);
        ulong t = await MixerTrackStructAsync(track, layout, ct);
        string name = await MixerTrackNameAsync(track, layout, ct);
        long vol = await GetMixerVolumeAsync(track, ct);
        int pan = await GetMixerPanAsync(track, ct);
        string state = t != 0 ? await MixerTrackStateFlagsAsync(t, layout, ct) : "";
        sb.Append($"Mixer track {track} '{name}': vol={vol}{(pan != 0 ? $" pan={pan}" : "")}{state}\n");

        var fx = new StringBuilder();
        for (int s = 0; s < 10; s++)
        {
            ulong so = await MixerSlotObjAsync(track, s, layout, ct);
            if (so == 0) continue;
            int idx = await AI32Async(so + (ulong)layout.EffectIndexOffset, ct);
            if (idx < 0) continue;
            ulong namePtr = await APtrAsync(so + (ulong)layout.EffectNameOffset, ct);
            fx.Append($"slot {s}: {await ReadDelphiStringAsync(namePtr, ct)}\n");
        }
        sb.Append(fx.Length == 0 ? "no effects loaded\n" : fx.ToString());
        if (t != 0) sb.Append(await DescribeSendsAsync(t, layout, ct));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Loads/replaces the named effect plugin into a mixer track's FX slot (0-9).</summary>
    public async Task AddMixerEffectAsync(int track, int slot, string pluginName, CancellationToken ct = default)
    {
        var layout = await MixerLayoutAsync(ct);
        string path = ResolveFstPath(pluginName, effects: true);
        ulong so = await MixerSlotObjAsync(track, slot, layout, ct);
        if (so == 0) throw new InvalidOperationException($"Mixer slot {track}/{slot} not found.");
        await LoadIntoMixerSlotAsync(so, 0xFFFFFFFDu, path, layout, ct);  // mode -3 = insert
        await RefreshMixerSlotAsync(track, slot, ct);
    }

    /// <summary>Refresh a mixer FX slot after loading/clearing its plugin (dispatch id
    /// base(track,slot) + 0x70001F00, flags 0x251 — the slot-changed notification).</summary>
    private Task RefreshMixerSlotAsync(int track, int slot, CancellationToken ct)
        => DispatchCommandAsync((uint)(((track * 0x40 + slot) << 16) + 0x70001F00), 1, 0x251, ct);

    /// <summary>Clears the plugin from a mixer track's FX slot.</summary>
    public async Task RemoveMixerEffectAsync(int track, int slot, CancellationToken ct = default)
    {
        var layout = await MixerLayoutAsync(ct);
        string? install = FlInstallDir() ?? throw new InvalidOperationException("FL Studio not running.");
        string del = Path.Combine(install, "Data", "System", "(delete) effect.fst");
        ulong so = await MixerSlotObjAsync(track, slot, layout, ct);
        if (so == 0) return;
        await LoadIntoMixerSlotAsync(so, 0xFFFFFFFEu, del, layout, ct);  // mode -2 = clear
        await RefreshMixerSlotAsync(track, slot, ct);
    }

    /// <summary>Copies the effect type from one FX slot to another (type only, not parameter state).</summary>
    public async Task CloneMixerEffectAsync(int track, int fromSlot, int toSlot, CancellationToken ct = default)
    {
        var layout = await MixerLayoutAsync(ct);
        ulong so = await MixerSlotObjAsync(track, fromSlot, layout, ct);
        if (so == 0) throw new InvalidOperationException($"Mixer slot {track}/{fromSlot} not found.");
        int idx = await AI32Async(so + (ulong)layout.EffectIndexOffset, ct);
        if (idx < 0) throw new InvalidOperationException($"Mixer slot {track}/{fromSlot} is empty.");
        string name = await ReadDelphiStringAsync(await APtrAsync(so + (ulong)layout.EffectNameOffset, ct), ct);
        await AddMixerEffectAsync(track, toSlot, name, ct);
    }
}
