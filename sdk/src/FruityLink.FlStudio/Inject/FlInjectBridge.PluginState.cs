using System.IO;
using System.Linq;
using System.Text;

namespace FruityLink.FlStudio.Inject;

// Plugin state / preset FILE loading into an already-hosted plugin (channel generator or mixer FX slot).
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
//
// Mechanism (re/generated/preset-C-files.md, live-verified on FL 26.1.3.5570 with Serum 2 VST3):
//   inst = (*(*host+0x20))(host)  with host = *(obj+0x38)+0x48   (the SAME resolution ResolvePluginAsync uses)
//   (*(*inst+8))(inst, 0x12, 0, utf8Path)                          = "load state/preset from file"
// The path is a UTF-8 char* (NOT a Delphi UnicodeString). The dispatcher lives in the wrapper/plugin
// module, not FLEngine, so the call is SEH-guarded (callabs) instead of module-range guarded.
// Opcode 0x12 restores state into the CURRENT instance: no generator swap, no re-instantiation.
public sealed partial class FlInjectBridge
{
    private const uint PluginDispatchLoadStateFile = 0x12;

    /// <summary>Load a plugin state/preset file into the generator already hosted by a channel.</summary>
    public async Task<string> LoadChannelPluginStateAsync(int channel, string path, bool useChannelLoader = false, CancellationToken ct = default)
    {
        LogOp("LoadChannelPluginState", $"channel={channel} path={path} channelLoader={useChannelLoader}");
        string full = ResolveStateFile(path);
        ulong obj = await ChannelObjAsync(channel, ct);
        if (obj == 0) throw new InvalidOperationException($"Channel {channel} not found.");
        await RequireGeneratorAsync(obj, channel, "load a plugin state file into", ct);
        var (inst, count, _) = await ResolvePluginAsync(channel, -1, ct);
        if (inst == 0)
            throw new InvalidOperationException($"Channel {channel} has no hosted-plugin instance; built-in Sampler channels do not accept plugin state files.");
        string pluginName = await TryReadPluginHolderNameAsync(obj, ct);
        EnsureStateFileTargetsPlugin(full, pluginName);
        byte[]? before = await TryReadChannelStateRecordAsync(channel, ct);

        // Live (FL 26.1.3, Serum 2): the channel loader applied no state for .vstpreset/.SerumPreset files
        // but RENAMED the channel to the file's base name. Only FL's own .fst goes through this route.
        if (useChannelLoader && !full.EndsWith(".fst", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "useChannelLoader only accepts FL .fst files: for other formats FL's channel loader applies no state and renames the channel to the file name.");
        string route;
        if (useChannelLoader || RequiresChannelLoader(full, pluginName))
        {
            string why = useChannelLoader
                ? "requested"
                : "automatic for an FL-native generator .fst: the wrapper dispatcher is a no-op for these";
            string restored = await LoadThroughChannelLoaderAsync(channel, obj, full, ct);
            route = $"FL's channel loader ({why}; {restored})";
        }
        else
        {
            await DispatchLoadStateFileAsync(inst, full, ct);
            route = "the wrapper's state-file dispatcher (opcode 0x12)";
        }
        await RefreshRackAsync(ct);

        var (instAfter, countAfter, _) = await ResolvePluginAsync(channel, -1, ct);
        string nameAfter = await TryReadPluginHolderNameAsync(obj, ct);
        byte[]? after = await TryReadChannelStateRecordAsync(channel, ct);
        return DescribeStateLoad($"channel {channel}", pluginName, nameAfter, inst, instAfter, count, countAfter, before, after, full, route);
    }

    /// <summary>Load a plugin state/preset file into the effect already loaded in a mixer FX slot.</summary>
    public async Task<string> LoadMixerEffectStateAsync(int track, int slot, string path, CancellationToken ct = default)
    {
        LogOp("LoadMixerEffectState", $"track={track} slot={slot} path={path}");
        string full = ResolveStateFile(path);
        await ValidateAddressableMixerTrackAsync(track, ct);
        ulong obj = await MixerSlotObjAsync(track, slot, ct);
        if (obj == 0 || await AI32Async(obj + 0x64, ct) < 0)
            throw new InvalidOperationException($"Mixer track {track} FX slot {slot} is empty.");
        var (inst, count, _) = await ResolvePluginAsync(track, slot, ct);
        if (inst == 0) throw new InvalidOperationException($"Mixer track {track} FX slot {slot} has no hosted-plugin instance.");
        string pluginName = await TryReadPluginHolderNameAsync(obj, ct);
        EnsureStateFileTargetsPlugin(full, pluginName);
        byte[]? before = await TryReadMixerStateRecordAsync(track, slot, ct);

        await DispatchLoadStateFileAsync(inst, full, ct);

        var (instAfter, countAfter, _) = await ResolvePluginAsync(track, slot, ct);
        string nameAfter = await TryReadPluginHolderNameAsync(obj, ct);
        byte[]? after = await TryReadMixerStateRecordAsync(track, slot, ct);
        return DescribeStateLoad($"mixer track {track} slot {slot}", pluginName, nameAfter, inst, instAfter, count, countAfter, before, after, full,
            "the wrapper's state-file dispatcher (opcode 0x12)");
    }

    // ---- route selection: wrapper dispatcher vs FL's channel loader ----

    /// <summary>FL's own VST/VST3 host: a channel (or FX slot) that hosts a WRAPPED third-party plugin reports this
    /// as its plugin-holder name — Serum 2 reads "Fruity Wrapper", the same name its FLP plugin-name record carries
    /// (see <c>FlpPluginStateReader</c>). Any other name is one of FL's own generators (Sytrus, Harmor, GMS, ...).</summary>
    private const string FlWrapperPluginName = "Fruity Wrapper";

    /// <summary>True when a <c>.fst</c> preset must go through FL's channel file loader instead of the wrapper
    /// dispatcher, because opcode 0x12 does nothing for it. Live (FL 26.1.3.5570, 2026-09-17): a Sytrus factory
    /// preset from <c>Data/Patches/Plugin presets/Generators/Sytrus</c> sent through the dispatcher left the state
    /// record byte-identical ("state record unchanged (1271 bytes ...)"), while the channel loader changed 99% of
    /// it; Harmor behaved the same way (47.5% changed). Wrapped plugins keep the dispatcher route, which is
    /// live-verified for them and does not disturb the channel. An unknown holder name ("" — an unexpected layout,
    /// see <see cref="TryReadPluginHolderNameAsync"/>) also keeps the dispatcher route, so a failed name read can
    /// never silently change which route a caller gets.</summary>
    internal static bool RequiresChannelLoader(string fullPath, string pluginName)
        => fullPath.EndsWith(".fst", StringComparison.OrdinalIgnoreCase)
           && pluginName.Length > 0
           && !pluginName.Equals(FlWrapperPluginName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Load a <c>.fst</c> through FL's channel file loader (the drag-and-drop path) with the channel's own
    /// identity preserved. That loader treats the file as a channel to build, not as state to apply: live
    /// (FL 26.1.3.5570, 2026-09-17) a Sytrus/Harmor preset load left the channel MUTED and RENAMED to the preset's
    /// base name ("Sync Lead", "Rhodes"), while the mixer route survived. Name, mute and mixer route are therefore
    /// snapshotted before the load and written back after it, each only when it actually moved (a redundant SET
    /// would add an undo step and a bus notification for nothing). Returns the "restored ..." text for the
    /// verification line. A channel with no name of its own reads back as "Channel N" (the shared
    /// <see cref="GetChannelNameCoreAsync"/> fallback), so such a channel is restored to that literal name.</summary>
    private async Task<string> LoadThroughChannelLoaderAsync(int channel, ulong obj, string fullPath, CancellationToken ct)
    {
        string nameBefore = await GetChannelNameCoreAsync(obj, channel, ct);
        bool mutedBefore = await GetChannelMutedAsync(channel, ct);
        int routeBefore = await GetChannelFxRouteAsync(channel, ct);

        await LoadIntoChannelAsync(obj, fullPath, 0, ct);

        string nameAfter = await GetChannelNameAsync(channel, ct);
        bool mutedAfter = await GetChannelMutedAsync(channel, ct);
        int routeAfter = await GetChannelFxRouteAsync(channel, ct);
        if (!string.Equals(nameAfter, nameBefore, StringComparison.Ordinal)) await SetChannelNameAsync(channel, nameBefore, ct);
        if (mutedAfter != mutedBefore) await SetChannelMutedAsync(channel, mutedBefore, ct);
        if (routeAfter != routeBefore) await SetChannelFxRouteAsync(channel, routeBefore, ct);
        return DescribeRestoredChannelState(nameBefore, nameAfter, mutedBefore, mutedAfter, routeBefore, routeAfter);
    }

    /// <summary>The "restored ..." half of the verification line: which of the channel's name, mute state and mixer
    /// route the channel loader changed, and what each was put back to. Says so explicitly when the loader left all
    /// three alone, so the line never implies a restore that did not happen.</summary>
    internal static string DescribeRestoredChannelState(string nameBefore, string nameAfter, bool mutedBefore, bool mutedAfter,
        int routeBefore, int routeAfter)
    {
        var restored = new List<string>();
        if (!string.Equals(nameAfter, nameBefore, StringComparison.Ordinal)) restored.Add($"name '{nameAfter}' -> '{nameBefore}'");
        if (mutedAfter != mutedBefore) restored.Add($"{(mutedAfter ? "muted" : "unmuted")} -> {(mutedBefore ? "muted" : "unmuted")}");
        if (routeAfter != routeBefore) restored.Add($"mixer route {routeAfter} -> {routeBefore}");
        return restored.Count == 0
            ? "name, mute and mixer route survived the load, nothing to restore"
            : "restored " + string.Join(", ", restored);
    }

    // ---- read state ----
    // No wrapper "save state to file" / getChunk opcode is recovered for FL 26 (re/generated/preset-C-files.md
    // only names 0x12 = load, and the .fst branch of FL's own loader goes through the project reader with mode
    // 0x30). The confident route is FL's own serializer: write a project copy through FLpr_WriteFlpFile (the
    // SaveCopyAsync path, which changes no project identity) and extract the plugin-data record (event 213)
    // for the channel/slot. That record is exactly what FL stores for the plugin, so it round-trips with 0x12.
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;
    private const int MaxStateBytes = 16 * 1024 * 1024;

    /// <summary>Read the generator's current wrapper state (base64 of its FLP plugin-data record).</summary>
    public async Task<string> GetChannelPluginStateAsync(int channel, CancellationToken ct = default)
    {
        LogOp("GetChannelPluginState", $"channel={channel}");
        ulong obj = await ChannelObjAsync(channel, ct);
        if (obj == 0) throw new InvalidOperationException($"Channel {channel} not found.");
        await RequireGeneratorAsync(obj, channel, "read the plugin state of", ct);
        byte[] state = await ExtractStateAsync(flp => FlpPluginStateReader.FindChannelState(flp, channel), ct)
            ?? throw new InvalidOperationException(
                $"Channel {channel} stores no plugin-data record in the project snapshot (built-in Sampler channels keep no wrapper state).");
        return EncodeState(state);
    }

    /// <summary>Read a mixer FX slot's current wrapper state (base64 of its FLP plugin-data record).</summary>
    public async Task<string> GetMixerEffectStateAsync(int track, int slot, CancellationToken ct = default)
    {
        LogOp("GetMixerEffectState", $"track={track} slot={slot}");
        await ValidateAddressableMixerTrackAsync(track, ct);
        ulong obj = await MixerSlotObjAsync(track, slot, ct);
        if (obj == 0 || await AI32Async(obj + 0x64, ct) < 0)
            throw new InvalidOperationException($"Mixer track {track} FX slot {slot} is empty.");
        byte[] state = await ExtractStateAsync(flp => FlpPluginStateReader.FindMixerSlotState(flp, track, slot), ct)
            ?? throw new InvalidOperationException(
                $"Mixer track {track} FX slot {slot} has no plugin-data record in the project snapshot.");
        return EncodeState(state);
    }

    /// <summary>Channels whose generator id (+0x64) is negative host no plugin: automation clips (registered in
    /// the target-link registry) and built-in Sampler / audio-clip / layer channels. Name the kind and the remedy
    /// instead of the former "automation/bus channel" guess, which misclassified Sampler channels (Parking Lot
    /// Moon, 2026-09-14).</summary>
    private async Task RequireGeneratorAsync(ulong obj, int channel, string action, CancellationToken ct)
    {
        if (await AI32Async(obj + 0x64, ct) >= 0) return;
        string? targets = await DescribeAutomationTargetsAsync(obj, ct);
        if (!string.IsNullOrEmpty(targets))
            throw new InvalidOperationException(
                $"Cannot {action} channel {channel}: it is an automation clip (automates {targets}) and hosts no plugin.");
        throw new InvalidOperationException(
            $"Cannot {action} channel {channel}: it is a built-in Sampler channel (or an audio clip / layer), which hosts no " +
            "generator plugin and keeps no wrapper state. Sampler settings are channel controls, not plugin parameters: use " +
            "channel volume/pan/pitch/mute/routing (fl.channels[n].volume ...), replace_channel_sample " +
            "(fl.channels[n].replace_sample(path)) and the sample operations; see docs/capabilities.md 'Important boundaries'.");
    }

    /// <summary>Snapshot the project and extract one plugin-data record. A framing error from the reader
    /// (InvalidDataException: the FLP event stream could not be walked) is retried once after a short settle and a
    /// fresh snapshot, because FL may still be finishing a load when the first copy is written; a second failure
    /// surfaces the reader's message (event, offset, writer build and remedy) as an operation failure instead of an
    /// opaque "FL event 254 is truncated".</summary>
    private async Task<byte[]?> ExtractStateAsync(Func<byte[], byte[]?> find, CancellationToken ct)
    {
        try { return find(await SnapshotProjectBytesAsync(ct)); }
        catch (InvalidDataException first)
        {
            LogOp("ExtractState", $"retry after reader error: {first.Message}");
            await Task.Delay(StateSnapshotRetryDelayMs, ct);
            try { return find(await SnapshotProjectBytesAsync(ct)); }
            catch (InvalidDataException second)
            {
                throw new InvalidOperationException(
                    $"The plugin state could not be extracted from the project snapshot (two attempts): {second.Message}", second);
            }
        }
    }

    private const int StateSnapshotRetryDelayMs = 250;

    /// <summary>Write a throwaway project copy to the temp directory through FL's direct writer and return its
    /// bytes. The file is deleted before returning; the live project's path, title and dirty flag are untouched.</summary>
    private async Task<byte[]> SnapshotProjectBytesAsync(CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fruitylink-state-{Guid.NewGuid():N}.flp");
        try
        {
            await SaveCopyAsync(path, ct);
            var info = new FileInfo(path);
            if (!info.Exists) throw new InvalidOperationException("FL Studio reported success but wrote no project snapshot.");
            if (info.Length > MaxSnapshotBytes)
                throw new InvalidOperationException($"The project snapshot is {info.Length} bytes; state extraction is limited to {MaxSnapshotBytes} bytes.");
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string EncodeState(byte[] state)
    {
        if (state.Length > MaxStateBytes)
            throw new InvalidOperationException($"The plugin state is {state.Length} bytes; results are limited to {MaxStateBytes} bytes.");
        return Convert.ToBase64String(state);
    }

    /// <summary>(*(*inst+8))(inst, 0x12, 0, utf8Path): the wrapper's "load state/preset from file" dispatcher
    /// opcode. The UTF-8 path (NUL-terminated) is staged in the scratch buffer for the duration of the call.</summary>
    private async Task DispatchLoadStateFileAsync(ulong inst, string fullPath, CancellationToken ct)
    {
        ulong vti = await APtrAsync(inst, ct);
        ulong fn = await APtrAsync(vti + 8, ct);
        if (fn == 0) throw new InvalidOperationException("Plugin dispatcher pointer is null.");
        byte[] utf8 = Encoding.UTF8.GetBytes(fullPath);
        if (utf8.Length + 1 > (int)ScratchOutputSlotOffset - 0x40)
            throw new ArgumentOutOfRangeException(nameof(fullPath), "The preset path exceeds the native scratch buffer capacity.");
        using var scratch = await LeaseScratchAsync(ct).ConfigureAwait(false);
        ulong pathPtr = scratch.Address + 0x40;
        var buf = new byte[utf8.Length + 1];
        utf8.CopyTo(buf, 0);
        await PokeAbsAsync(pathPtr, buf, ct);
        // Plugin loads can block on file IO and UI refresh; allow more than the default guard timeout.
        // Same budget as generator/effect instantiation (PluginInstantiationTimeoutMs).
        await CallAbsAsync(fn, new ulong[] { inst, PluginDispatchLoadStateFile, 0, pathPtr }, ct,
            timeoutMs: PluginInstantiationTimeoutMs);
    }

    private static string ResolveStateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A preset/state file path is required.", nameof(path));
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new InvalidOperationException($"Preset/state file not found: {full}");
        return full;
    }

    /// <summary>Identity guard for FL <c>.fst</c> presets: the file embeds the plugin's display name, as UTF-16 for
    /// wrapped VST/VST3 plugins (Serum 2) and as a single-byte ANSI/UTF-8 string for FL's own generators (Sytrus,
    /// Harmor, ... store a NUL-terminated "Sytrus" right after the FLdt version tag); if the hosted plugin's name is known and absent
    /// in BOTH encodings, the preset targets another plugin. Other formats (.vstpreset/.fxp) are validated by the
    /// wrapper itself (class id / fxID) and pass through.</summary>
    internal static void EnsureStateFileTargetsPlugin(string fullPath, string pluginName)
    {
        if (pluginName.Length == 0 || !fullPath.EndsWith(".fst", StringComparison.OrdinalIgnoreCase)) return;
        byte[] data = File.ReadAllBytes(fullPath);
        byte[] utf16 = Encoding.Unicode.GetBytes(pluginName);
        byte[] ansi = Encoding.UTF8.GetBytes(pluginName);
        if (data.AsSpan().IndexOf(utf16) < 0 && data.AsSpan().IndexOf(ansi) < 0)
            throw new InvalidOperationException(
                $"Preset '{Path.GetFileName(fullPath)}' does not name the hosted plugin '{pluginName}'; refusing to load a state file for a different plugin.");
    }

    /// <summary>The channel's FLP plugin-data record, or null when a snapshot cannot be taken or parsed. Used only
    /// as load evidence, so snapshot and reader failures never fail the load itself (before 2026-09-14 an
    /// InvalidDataException from the reader escaped here and aborted every load on FL 5570 snapshots).</summary>
    private async Task<byte[]?> TryReadChannelStateRecordAsync(int channel, CancellationToken ct)
    {
        try { return await ExtractStateAsync(flp => FlpPluginStateReader.FindChannelState(flp, channel), ct); }
        catch (InvalidOperationException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
    }

    private async Task<byte[]?> TryReadMixerStateRecordAsync(int track, int slot, CancellationToken ct)
    {
        try { return await ExtractStateAsync(flp => FlpPluginStateReader.FindMixerSlotState(flp, track, slot), ct); }
        catch (InvalidOperationException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (IOException) { return null; }
    }

    /// <summary>Evidence text comparing the wrapper's plugin-data record before and after a load. Live (FL 26.1.3,
    /// Serum 2) the earlier "N/12 sampled parameter values changed" line never moved on loads that demonstrably
    /// changed the sound (the sampled indices missed the plugin's live parameters), so the record itself is
    /// compared: sizes, a short SHA-256 of each side, and the number of differing bytes (position-wise over the
    /// common prefix, plus the size delta). No decoded-section diff is attempted: the wrapper record embeds the
    /// plugin's opaque chunk. A few differing bytes can be serializer noise (Ember Tides v007: untouched channels
    /// re-serialised with float noise); a large fraction indicates a loaded state. When either snapshot failed
    /// the line says so instead of claiming "no change".</summary>
    internal static string DescribeStateEvidence(byte[]? before, byte[]? after)
    {
        if (before is null || after is null)
            return "state record unavailable (no before/after project snapshot could be taken, so nothing was compared; " +
                   "verify with parameter displays in a separate request or an isolated render)";
        if (before.AsSpan().SequenceEqual(after))
            return $"state record unchanged ({after.Length} bytes, sha256 {ShortHash(after)}; a wrong-format or already-active file leaves the state untouched)";
        int limit = Math.Min(before.Length, after.Length);
        int first = 0;
        while (first < limit && before[first] == after[first]) first++;
        int differing = Math.Abs(before.Length - after.Length);
        for (int i = first; i < limit; i++) if (before[i] != after[i]) differing++;
        int span = Math.Max(before.Length, after.Length);
        double percent = span == 0 ? 0 : 100.0 * differing / span;
        return $"state record changed ({before.Length} -> {after.Length} bytes, sha256 {ShortHash(before)} -> {ShortHash(after)}, " +
               $"{differing} differing bytes = {percent.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%, first at byte {first})";
    }

    /// <summary>First 8 hex characters of the record's SHA-256: enough to compare lines across requests.</summary>
    internal static string ShortHash(byte[] data)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data), 0, 4).ToLowerInvariant();

    private static string DescribeStateLoad(string target, string nameBefore, string nameAfter, ulong instBefore, ulong instAfter,
        int countBefore, int countAfter, byte[]? before, byte[]? after, string fullPath, string route)
    {
        string swap = instBefore == instAfter ? "same instance" : "INSTANCE REPLACED";
        string name = string.Equals(nameBefore, nameAfter, StringComparison.Ordinal) ? $"'{nameBefore}'" : $"'{nameBefore}' -> '{nameAfter}'";
        return $"{target}: loaded '{Path.GetFileName(fullPath)}' into {name} via {route} ({swap}; params {countBefore}->{countAfter}; " +
               $"{DescribeStateEvidence(before, after)}). Parameter displays are reliable in a separate request.";
    }
}
