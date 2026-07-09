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
    // ---- mixer track names (RE: re/generated/controls-mixer.md §"Mixer track array") --------------
    // g_MixerTrackArrayPtr @ 0x14A7EB0 is a POINTER; array base = *(void**)0x14A7EB0. Track N struct =
    // base + N*0x1474. Custom name = Delphi UnicodeString ptr @ +0x0C (0 = default-by-type); track type
    // @ +0x08 (0=Master, 1=Insert "Insert %d", 2=Current). Count = *(int*)( *(void**)0x14A9850 )
    // (=127 at rest: master + 125 inserts + current).
    private const ulong MixerTrackStride = 0x1474;

    // Track state bytes within the 0x1474 struct (re/generated/controls-mixer.md §track struct):
    // +0x18 enabled (0 = muted — FL's isTrackMuted is literally "struct+0x18 == 0"), +0x19 selected,
    // +0x1A solo, +0x1B mute-lock. Send table @ +0x2E4: per-destination 8 bytes
    // { +0 level int32 = levelFloat*16000 (unity = 16000), +4 active byte } — the SAME slots
    // SetMixerSendAsync writes, so reads and writes can never disagree on layout.
    private const int MixerFieldEnabled = 0x18;
    private const ulong MixerFieldSendTable = 0x2E4;

    /// <summary>Base address of a mixer track's struct, or 0 when unavailable/out of range.</summary>
    private async Task<ulong> MixerTrackStructAsync(int track, CancellationToken ct)
    {
        if (track < 0 || track >= await GetMixerTrackCountAsync(ct)) return 0;
        ulong arrayBase = await GPtrAsync("14a7eb0", ct);
        return arrayBase == 0 ? 0 : arrayBase + (ulong)track * MixerTrackStride;
    }

    /// <summary>" MUTED"/" SOLO" markers from the track's state bytes (+0x18 enabled, +0x1A solo).</summary>
    private async Task<string> MixerTrackStateFlagsAsync(ulong trackStruct, CancellationToken ct)
    {
        byte[] st = await PeekAbsAsync(trackStruct + (ulong)MixerFieldEnabled, 4, ct);
        return (st[0] == 0 ? " MUTED" : "") + (st[2] != 0 ? " SOLO" : "");
    }

    /// <summary>Read a mixer track's mute state (enabled byte +0x18 == 0 ⇒ muted).</summary>
    public async Task<bool> GetMixerTrackMutedAsync(int track, CancellationToken ct = default)
    {
        ulong t = await MixerTrackStructAsync(track, ct);
        if (t == 0) throw new InvalidOperationException($"Mixer track {track} not available.");
        return (await PeekAbsAsync(t + (ulong)MixerFieldEnabled, 1, ct))[0] == 0;
    }

    /// <summary>Mute/unmute a mixer track by writing the enabled byte (+0x18) — the exact model field
    /// FL's own mute op targets (FLmx_SetTrackEnabledCore; its full arg contract isn't decompiled, so
    /// the byte write + routing refresh is the safe path, same pattern as playlist-track mute).</summary>
    public async Task SetMixerTrackMutedAsync(int track, bool muted, CancellationToken ct = default)
    {
        LogOp("SetMixerTrackMuted", $"track={track} muted={muted}");
        ulong t = await MixerTrackStructAsync(track, ct);
        if (t == 0) throw new InvalidOperationException($"Mixer track {track} not available.");
        await PokeAbsAsync(t + (ulong)MixerFieldEnabled, new byte[] { (byte)(muted ? 0 : 1) }, ct);
        ulong mgr = await APtrAsync(await GPtrAsync("14a99a0", ct), ct);
        if (mgr > 0x10000) await CallAsync("11a5d20", new ulong[] { mgr }, ct);  // FLmx_RefreshRouting
    }

    /// <summary>A track's active sends read from the send table in ONE peek: "sends: ->0 'Master'
    /// (1.0), ->5 'Reverb' (0.5)". This is the read half SetMixerSendAsync never had — without it the
    /// model couldn't see existing routing at all.</summary>
    private async Task<string> DescribeSendsAsync(ulong trackStruct, CancellationToken ct)
    {
        int count = await GetMixerTrackCountAsync(ct);
        byte[] table = await PeekAbsAsync(trackStruct + MixerFieldSendTable, count * 8, ct);
        var sends = new List<string>();
        for (int d = 0; d < count; d++)
        {
            if (table[d * 8 + 4] == 0) continue;   // +4 = active byte
            double level = BitConverter.ToInt32(table, d * 8) / 16000.0;
            string dn = await GetMixerTrackNameAsync(d, ct);
            sends.Add($"->{d} '{dn}' ({level:0.###})");
        }
        return sends.Count == 0 ? "sends: none" : "sends: " + string.Join(", ", sends);
    }

    /// <summary>Number of mixer tracks (127 at rest: master + 125 inserts + current).</summary>
    public async Task<int> GetMixerTrackCountAsync(CancellationToken ct = default)
    {
        ulong cntObj = await GPtrAsync("14a9850", ct);   // *(void**)g_pMixerTrackCount
        if (cntObj == 0) return 127;
        int n = await AI32Async(cntObj, ct);
        return n is > 0 and <= 1000 ? n : 127;
    }

    /// <summary>Effective mixer track name: the custom name if set, else FL's default by type
    /// (Master / "Insert &lt;n&gt;" / Current). Symmetric with <see cref="SetMixerSendAsync"/> addressing.</summary>
    public async Task<string> GetMixerTrackNameAsync(int track, CancellationToken ct = default)
    {
        if (track < 0) return "";
        ulong arrayBase = await GPtrAsync("14a7eb0", ct);   // *(void**)g_MixerTrackArrayPtr
        if (arrayBase == 0) return "";
        ulong trackStruct = arrayBase + (ulong)track * MixerTrackStride;
        ulong namePtr = await APtrAsync(trackStruct + 0x0C, ct);
        string custom = namePtr == 0 ? "" : await ReadDelphiStringAsync(namePtr, ct);
        if (!string.IsNullOrEmpty(custom)) return custom;
        int type = await AI32Async(trackStruct + 0x08, ct);
        return type switch { 0 => "Master", 2 => "Current", _ => $"Insert {track}" };
    }

    /// <summary>Rename a mixer track. Assigns the name field (+0x0C, the exact field
    /// <see cref="GetMixerTrackNameAsync"/> reads) via FL's Delphi_UStrAsg, which DEEP-COPIES the const into an
    /// FL-owned heap string that persists (a raw poke of a scratch-const pointer would dangle). Empty name
    /// pokes 0 to clear back to the type default. Avoids the unconfirmed hub setter 0x11C2810 + its cascade.</summary>
    public async Task SetMixerTrackNameAsync(int track, string name, CancellationToken ct = default)
    {
        LogOp("SetMixerTrackName", $"track={track}");
        ulong t = await MixerTrackStructAsync(track, ct);
        if (t == 0) throw new InvalidOperationException($"Mixer track {track} not available.");
        if (string.IsNullOrEmpty(name))
            await PokeAbsAsync(t + 0x0C, BitConverter.GetBytes(0UL), ct);          // clear -> default-by-type
        else
        {
            ulong str = await MakeOwnedDelphiStringAsync(name, ct);
            await UStrAsgAsync(t + 0x0C, str, ct);                                 // deep-copy into the name field
        }
        ulong mgr = await APtrAsync(await GPtrAsync("14a99a0", ct), ct);
        if (mgr > 0x10000) await CallAsync("11a5d20", new ulong[] { mgr }, ct);    // FLmx_RefreshRouting (repaint)
    }

    /// <summary>
    /// Lists mixer tracks that have a CUSTOM name (plus Master) as "index: name", so a caller can
    /// resolve a bus/track NAME to the numeric index every other mixer tool needs. Unnamed inserts
    /// are omitted (they default to "Insert &lt;n&gt;" at track &lt;n&gt;) to keep the list signal-rich.
    /// </summary>
    public async Task<string> ListMixerTracksAsync(CancellationToken ct = default)
    {
        ulong arrayBase = await GPtrAsync("14a7eb0", ct);
        if (arrayBase == 0) return "(mixer not available)";
        int count = await GetMixerTrackCountAsync(ct);

        var named = new List<string>();
        for (int t = 0; t < count; t++)
        {
            ct.ThrowIfCancellationRequested();
            ulong namePtr = await APtrAsync(arrayBase + (ulong)t * MixerTrackStride + 0x0C, ct);
            string custom = namePtr == 0 ? "" : await ReadDelphiStringAsync(namePtr, ct);
            if (string.IsNullOrEmpty(custom) && t != 0) continue;
            // Current level + MUTED/SOLO ride along on every listed track (they're the ones a mix
            // turn touches), so the model can reason relatively instead of setting blind.
            long vol = await GetMixerVolumeAsync(t, ct);
            int pan = await GetMixerPanAsync(t, ct);
            string state = await MixerTrackStateFlagsAsync(arrayBase + (ulong)t * MixerTrackStride, ct);
            named.Add($"{t}: {(string.IsNullOrEmpty(custom) ? "Master" : custom)} vol={vol}"
                + (pan != 6400 ? $" pan={pan}" : "") + state);
        }

        int lastInsert = count >= 2 ? count - 2 : count - 1;
        var sb = new System.Text.StringBuilder();
        sb.Append($"Mixer: {count} tracks (0=Master, 1-{lastInsert}=Inserts, {count - 1}=Current). ");
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
        ulong mgr = await APtrAsync(await GPtrAsync("14a99a0", ct), ct); // *(*(0x14A99A0))
        ulong baseArr = await GPtrAsync("14a7eb0", ct);  // *(0x14A7EB0)
        await CallAsync("11a67f0", new ulong[] { mgr, (uint)srcTrack, (uint)dstTrack, 1, 1 }, ct);  // FLmx_SetRouteActiveCore
        ulong slot = baseArr + (ulong)srcTrack * MixerTrackStride + (ulong)dstTrack * 8 + 0x2E4;
        await PokeAbsAsync(slot, BitConverter.GetBytes((int)Math.Round(level * 16000)), ct);
        await CallAsync("11a5d20", new ulong[] { mgr }, ct);  // FLmx_RefreshRouting
    }

    // ============================ Mixer EQ gain ============================

    /// <summary>Set a mixer track EQ band gain (band 0=low,1=mid,2=high; value 0..0x40000000, ~0x20000000 = 0 dB).</summary>
    public Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default)
        => DispatchCommandAsync((uint)(((long)track << 22) + 0x70001FD0 + Math.Clamp(band, 0, 2)), unchecked((ulong)(uint)value), FlagSet, ct);

    /// <summary>Load/clear a plugin in a mixer FX slot: (*(*slot+0xF0))(slot, mode, path, 0, 1, 1).
    /// mode 0xFFFFFFFD = insert, 0xFFFFFFFE = clear.</summary>
    private async Task LoadIntoMixerSlotAsync(ulong slot, uint mode, string path, CancellationToken ct)
    {
        ulong strPtr = await WriteDelphiStringAsync(path, ct);
        await CallVtblAsync(slot, 0xF0, "Mixer slot load", new ulong[] { slot, mode, strPtr, 0, 1, 1 }, ct);
    }

    // ---- mixer FX slots ----

    private async Task<ulong> MixerSlotObjAsync(int track, int slot, CancellationToken ct)
    {
        if (track < 0 || track > 199) throw new InvalidOperationException($"Mixer track {track} out of range (0..199).");
        if (slot < 0 || slot > 9) throw new InvalidOperationException($"FX slot {slot} out of range (0..9).");
        ulong baseArr = await GPtrAsync("14a7eb0", ct);
        if (baseArr == 0) return 0;
        return await APtrAsync(baseArr + (ulong)track * MixerTrackStride + 0x1324 + (ulong)slot * 8, ct);
    }

    /// <summary>Inspects one mixer track end to end: name, current vol/pan, MUTED/SOLO state, loaded
    /// FX slots, and active sends with levels — the full picture a mix decision needs in one call
    /// (the FX-slot list alone left the model blind to levels and routing).</summary>
    public async Task<string> ListMixerEffectsAsync(int track, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        ulong t = await MixerTrackStructAsync(track, ct);
        string name = await GetMixerTrackNameAsync(track, ct);
        long vol = await GetMixerVolumeAsync(track, ct);
        int pan = await GetMixerPanAsync(track, ct);
        string state = t != 0 ? await MixerTrackStateFlagsAsync(t, ct) : "";
        sb.Append($"Mixer track {track} '{name}': vol={vol}{(pan != 6400 ? $" pan={pan}" : "")}{state}\n");

        var fx = new StringBuilder();
        for (int s = 0; s < 10; s++)
        {
            ulong so = await MixerSlotObjAsync(track, s, ct);
            if (so == 0) continue;
            int idx = await AI32Async(so + 0x64, ct);
            if (idx < 0) continue;
            ulong namePtr = await APtrAsync(so + 0x58, ct);
            fx.Append($"slot {s}: {await ReadDelphiStringAsync(namePtr, ct)}\n");
        }
        sb.Append(fx.Length == 0 ? "no effects loaded\n" : fx.ToString());
        if (t != 0) sb.Append(await DescribeSendsAsync(t, ct));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Loads/replaces the named effect plugin into a mixer track's FX slot (0-9).</summary>
    public async Task AddMixerEffectAsync(int track, int slot, string pluginName, CancellationToken ct = default)
    {
        string? path = ResolveFstPath(pluginName, effects: true)
            ?? throw new InvalidOperationException($"Effect plugin '{pluginName}' not found (try native_list_available_plugins).");
        ulong so = await MixerSlotObjAsync(track, slot, ct);
        if (so == 0) throw new InvalidOperationException($"Mixer slot {track}/{slot} not found.");
        await LoadIntoMixerSlotAsync(so, 0xFFFFFFFDu, path, ct);  // mode -3 = insert
        await RefreshMixerSlotAsync(track, slot, ct);
    }

    /// <summary>Refresh a mixer FX slot after loading/clearing its plugin (dispatch id
    /// base(track,slot) + 0x70001F00, flags 0x251 — the slot-changed notification).</summary>
    private Task RefreshMixerSlotAsync(int track, int slot, CancellationToken ct)
        => DispatchCommandAsync((uint)(((track * 0x40 + slot) << 16) + 0x70001F00), 1, 0x251, ct);

    /// <summary>Clears the plugin from a mixer track's FX slot.</summary>
    public async Task RemoveMixerEffectAsync(int track, int slot, CancellationToken ct = default)
    {
        string? install = FlInstallDir() ?? throw new InvalidOperationException("FL Studio not running.");
        string del = Path.Combine(install, "Data", "System", "(delete) effect.fst");
        ulong so = await MixerSlotObjAsync(track, slot, ct);
        if (so == 0) return;
        await LoadIntoMixerSlotAsync(so, 0xFFFFFFFEu, del, ct);  // mode -2 = clear
        await RefreshMixerSlotAsync(track, slot, ct);
    }

    /// <summary>Copies the effect type from one FX slot to another (type only, not parameter state).</summary>
    public async Task CloneMixerEffectAsync(int track, int fromSlot, int toSlot, CancellationToken ct = default)
    {
        ulong so = await MixerSlotObjAsync(track, fromSlot, ct);
        if (so == 0) throw new InvalidOperationException($"Mixer slot {track}/{fromSlot} not found.");
        int idx = await AI32Async(so + 0x64, ct);
        if (idx < 0) throw new InvalidOperationException($"Mixer slot {track}/{fromSlot} is empty.");
        string name = await ReadDelphiStringAsync(await APtrAsync(so + 0x58, ct), ct);
        await AddMixerEffectAsync(track, toSlot, name, ct);
    }
}
