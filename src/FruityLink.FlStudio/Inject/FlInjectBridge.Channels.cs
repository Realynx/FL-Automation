using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Channel rack: channel list/names, generator plugins, samples, plugin parameters, automation clips.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ============================ Channels ============================
    // Channel list = *(*(0x14A98D8)) (double-deref); count @ +0x10, items @ +0x8.

    private async Task<ulong> ChannelListAsync(CancellationToken ct)
    {
        ulong p0 = await GPtrAsync("14a98d8", ct);
        return p0 == 0 ? 0 : await APtrAsync(p0, ct);
    }

    public async Task<int> GetChannelCountAsync(CancellationToken ct = default)
    {
        ulong list = await ChannelListAsync(ct);
        return list == 0 ? 0 : await AI32Async(list + 0x10, ct);
    }

    private async Task<ulong> ChannelObjAsync(int index, CancellationToken ct)
    {
        if (index < 0) throw new InvalidOperationException($"Channel index {index} is invalid (must be >= 0).");
        ulong list = await ChannelListAsync(ct);
        if (list == 0) return 0;
        int count = await AI32Async(list + 0x10, ct);
        if (index >= count) throw new InvalidOperationException($"Channel {index} does not exist (only {count} channel(s)).");
        return await CallAsync("f00f80", new ulong[] { list, (uint)index }, ct);  // FLcr_ChannelListGetItem
    }

    /// <summary>Exclusively select a channel so the piano roll edits it. FLcr_SelectOneChannelByIndex.</summary>
    public async Task SelectChannelAsync(int index, CancellationToken ct = default)
    {
        int count = await GetChannelCountAsync(ct);
        if (index < 0 || index >= count) throw new InvalidOperationException($"Channel {index} does not exist (only {count} channel(s)).");
        await CallAsync("10e3eb0", new ulong[] { (uint)index }, ct);
    }

    public async Task<string> GetChannelNameAsync(int index, CancellationToken ct = default)
    {
        ulong ch = await ChannelObjAsync(index, ct);
        return ch == 0 ? $"Channel {index}" : await GetChannelNameCoreAsync(ch, index, ct);
    }

    private async Task<string> GetChannelNameCoreAsync(ulong ch, int index, CancellationToken ct)
    {
        // Delphi `function GetName: string` (vtbl+0x68) returns via a hidden out-param: getName(self, @result).
        // The result slot must be a valid (zeroed) UnicodeString var, or the assign derefs garbage and faults.
        ulong sc = await ZeroedScratchSlotAsync(0, ct);
        await CallVtblAsync(ch, 0x68, "Channel getName", new[] { ch, sc }, ct);
        ulong strPtr = await APtrAsync(sc, ct);
        string s = await ReadDelphiStringAsync(strPtr, ct);
        return string.IsNullOrEmpty(s) ? $"Channel {index}" : s;
    }

    /// <summary>Rename a channel via its Delphi setName (vtbl+0x70) — symmetric with the getName (vtbl+0x68)
    /// path above. Uses an FL-OWNED heap string so the setter's UStrAsg-share persists across scratch reuse
    /// and save/reload.</summary>
    public async Task SetChannelNameAsync(int index, string name, CancellationToken ct = default)
    {
        ulong ch = await ChannelObjAsync(index, ct);
        if (ch == 0) throw new InvalidOperationException($"Channel {index} not found.");
        LogOp("SetChannelName", $"index={index}");
        ulong str = await MakeOwnedDelphiStringAsync(name ?? string.Empty, ct);
        await CallVtblAsync(ch, 0x70, "Channel setName", new[] { ch, str }, ct);   // UStrAsg into the name field + notify
        await RefreshRackAsync(ct);
    }

    /// <summary>Toggle exclusive channel solo via FLcr_ApplyChannelSolo (the same op the rack's channel
    /// solo-click invokes; carries the exclusive-solo bookkeeping + undo step, non-modal). Toggle: solo again
    /// un-solos. Refreshes the rack so the strip repaints.</summary>
    public async Task SetChannelSoloAsync(int index, CancellationToken ct = default)
    {
        ulong ch = await ChannelObjAsync(index, ct);
        if (ch == 0) throw new InvalidOperationException($"Channel {index} not found.");
        LogOp("SetChannelSolo", $"index={index}");
        await CallAsync("e012f0", new ulong[] { ch, 0 }, ct);   // FLcr_ApplyChannelSolo(chObj, mode 0 = toggle)
        await RefreshRackAsync(ct);
    }

    /// <summary>Lists channels WITH the working state the model otherwise has to probe one call at a
    /// time (the "index-only mixer list" failure class): mixer route, mute, non-default vol/pan, and a
    /// no-generator marker. Defaults are omitted per line so a pristine rack stays one short line per
    /// channel; the header states the omission rule ONCE so the model can trust what absence means.</summary>
    public async Task<string> ListChannelsAsync(CancellationToken ct = default)
    {
        int n = await GetChannelCountAsync(ct);
        if (n <= 0 || n > 1000) return "(no channels)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++)
        {
            ulong ch = await ChannelObjAsync(i, ct);
            sb.Append(i).Append(": ").Append(ch == 0 ? $"Channel {i}" : await GetChannelNameCoreAsync(ch, i, ct));
            if (ch != 0 && await AI32Async(ch + 0x64, ct) < 0) sb.Append(" (automation/bus)");
            int route = await GetChannelFxRouteAsync(i, ct);
            if (route > 0) sb.Append(" ->mixer ").Append(route);
            if (await GetChannelMutedAsync(i, ct)) sb.Append(" MUTED");
            long vol = await GetChannelVolumeAsync(i, ct);
            if (vol != 10000) sb.Append(" vol=").Append(vol);
            int pan = await GetChannelPanAsync(i, ct);
            if (pan != 6400) sb.Append(" pan=").Append(pan);
            sb.Append('\n');
        }
        return $"{n} channels (vol/pan shown only when non-default; no ->mixer = routed to Master):\n"
            + sb.ToString().TrimEnd();
    }

    // ============================ Plugins / inserts ============================
    // Plugin database = .fst files under the user's "Plugin database\{Generators,Effects}" tree.
    // Loading a plugin = pass the full .fst path (Delphi string) to the host's load method:
    //   channel generator: (*(*ch + 0x150))(ch, fstPath, 0, 0x42)
    //   mixer FX slot:      (*(*slot + 0xF0))(slot, mode, fstPath, 0, 1, 1)  mode -3 insert, -2 clear

    private static string PluginDbDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Image-Line", "FL Studio", "Presets", "Plugin database");

    /// <summary>Lists installed plugins of a kind (generators or effects) by display name,
    /// newline-separated: one name per line lets the tool layer filter/cap by line without a
    /// separator ambiguity (plugin names legitimately contain commas and spaces).</summary>
    public Task<string> ListAvailablePluginsAsync(bool effects, CancellationToken ct = default)
    {
        string dir = Path.Combine(PluginDbDir, effects ? "Effects" : "Generators");
        if (!Directory.Exists(dir)) return Task.FromResult($"(plugin database not found at {dir})");
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(dir, "*.fst", SearchOption.AllDirectories))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        return Task.FromResult(names.Count == 0 ? "(none found)" : string.Join("\n", names));
    }

    /// <summary>Resolves a plugin display name to its .fst path in the plugin database.</summary>
    private static string? ResolveFstPath(string name, bool effects)
    {
        string dir = Path.Combine(PluginDbDir, effects ? "Effects" : "Generators");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.fst", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FlInstallDir()
    {
        try { return Path.GetDirectoryName(Process.GetProcessesByName("FL64").FirstOrDefault()?.MainModule?.FileName ?? string.Empty); }
        catch { return null; }
    }

    /// <summary>Load a file (.fst plugin or audio) into a channel via its host load method:
    /// (*(*ch+0x150))(ch, path, mode, 0x42). mode 0 = plugin/preset, 1 = load sample.</summary>
    private async Task LoadIntoChannelAsync(ulong ch, string path, uint mode, CancellationToken ct)
    {
        // FL's channel load path (vtbl+0x150) is NOT re-entrant across rapid successive instantiations:
        // a second load firing within ~10ms of the previous FAULTS (observed 22% on back-to-back Serum
        // adds; ≥140ms apart always succeeds — tools-20260708.log). Serialize all loads and SPACE them so
        // the VST scan/instantiate settles first. Static gate: parallel sub-agents share one bridge.
        await _channelLoadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long since = Environment.TickCount64 - _lastChannelLoadTicks;
            if (_lastChannelLoadTicks != 0 && since < MinChannelLoadSpacingMs)
                await Task.Delay((int)(MinChannelLoadSpacingMs - since), ct).ConfigureAwait(false);
            ulong strPtr = await WriteDelphiStringAsync(path, ct);
            await CallVtblAsync(ch, 0x150, "Channel load", new ulong[] { ch, strPtr, mode, 0x42 }, ct);
        }
        finally
        {
            _lastChannelLoadTicks = Environment.TickCount64;
            _channelLoadGate.Release();
        }
    }

    // Serializes + spaces FL plugin/sample instantiation (see LoadIntoChannelAsync) — the fix for the
    // back-to-back VST-load fault. Static because sub-agent kernels share one FlInjectBridge.
    private static readonly SemaphoreSlim _channelLoadGate = new(1, 1);
    private static long _lastChannelLoadTicks;
    private const int MinChannelLoadSpacingMs = 200;

    // ---- channel rack ----

    /// <summary>Describes a channel's loaded generator plugin: the PLUGIN's display name (not just the
    /// channel name, which the user may have renamed) + its param count, so the model knows what it is
    /// driving before reaching for param tools.</summary>
    public async Task<string> GetChannelPluginAsync(int channel, CancellationToken ct = default)
    {
        ulong ch = await ChannelObjAsync(channel, ct);
        if (ch == 0) return $"Channel {channel}: not found.";
        int gen = await AI32Async(ch + 0x64, ct);
        string name = await GetChannelNameCoreAsync(ch, channel, ct);
        // Automation clips register in the target-link registry regardless of their generator id, so
        // check links FIRST — "automates: Insert 3 volume" beats a generic generator report.
        string? targets = await DescribeAutomationTargetsAsync(ch, ct);
        if (!string.IsNullOrEmpty(targets)) return $"{name}: automation clip -> {targets}";
        if (gen < 0) return $"{name}: no generator (bus/automation)";
        string plugin = await TryReadPluginHolderNameAsync(ch, ct);
        int paramCount = await AI32Async(ch + 0x68, ct);
        string count = paramCount is > 0 and <= 100_000 ? $" ({paramCount} params)" : "";
        return plugin.Length > 0
            ? $"{name}: generator '{plugin}'{count}"
            : $"{name}: has a generator plugin{count}";
    }

    /// <summary>Best-effort plugin display name off the shared plugin-holder layout: +0x58 is the SAME
    /// name field <see cref="ListMixerEffectsAsync"/> reads on a mixer FX slot (channels and slots share
    /// the holder band +0x38..+0x68 — see <see cref="ResolvePluginAsync"/>). Falls back to "" on any
    /// fault or implausible decode, so an unexpected layout can only OMIT the name, never report a
    /// wrong one.</summary>
    private async Task<string> TryReadPluginHolderNameAsync(ulong obj, CancellationToken ct)
    {
        try
        {
            string s = await ReadDelphiStringAsync(await APtrAsync(obj + 0x58, ct), ct);
            return s.Length > 0 && !s.Any(char.IsControl) ? s : "";
        }
        catch (InvalidOperationException) { return ""; }
    }

    /// <summary>Adds a new channel-rack channel hosting the named generator plugin; returns its index.</summary>
    public async Task<int> AddChannelAsync(string pluginName, CancellationToken ct = default)
    {
        string? path = ResolveFstPath(pluginName, effects: false)
            ?? throw new InvalidOperationException($"Generator plugin '{pluginName}' not found (try native_list_available_plugins).");
        int before = await GetChannelCountAsync(ct);
        ulong ch = await CallAsync("f215e0", new ulong[] { (uint)before, 0, 0 }, ct);  // FLcr_InsertChannel
        if (ch == 0) throw new InvalidOperationException("Could not insert a channel.");
        await LoadIntoChannelAsync(ch, path, 0, ct);
        await RefreshRackAsync(ct);
        return before;
    }

    // ---- samples ----
    private static readonly string[] SampleExts = { ".wav", ".aif", ".aiff", ".mp3", ".ogg", ".flac", ".rx2" };

    /// <summary>The sample search roots with their entry tags: [P] = FL factory packs, [U] = the
    /// user's Image-Line documents content. Shared by the lister (emits tagged relative paths) and
    /// the resolver (maps them back to full paths), so the two can never drift apart.</summary>
    private static (string Root, string Tag)[] SampleRoots()
    {
        var roots = new List<(string, string)>();
        string? install = FlInstallDir();
        if (install != null) roots.Add((Path.Combine(install, "Data", "Patches", "Packs"), "[P]"));
        roots.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Image-Line", "FL Studio"), "[U]"));
        return roots.ToArray();
    }

    /// <summary>Resolve a sample reference back to a full path: accepts a full path, a root-tagged
    /// relative path from <see cref="ListSamplesAsync"/> ("[P]Drums\Kicks\x.wav"), or a bare relative
    /// path (tried against every root). Returns the input unchanged when nothing matches so the
    /// caller's file-not-found error shows exactly what the model passed.</summary>
    private static string ResolveSamplePath(string samplePath)
    {
        if (string.IsNullOrWhiteSpace(samplePath) || File.Exists(samplePath)) return samplePath;
        string p = samplePath.Trim();
        foreach (var (root, tag) in SampleRoots())
        {
            string rel = p.StartsWith(tag, StringComparison.OrdinalIgnoreCase) ? p[tag.Length..] : p;
            string full = Path.Combine(root, rel.TrimStart('\\', '/'));
            if (File.Exists(full)) return full;
        }
        return samplePath;
    }

    /// <summary>Lists audio samples from FL's factory packs + the user's content, optionally filtered
    /// by name (path substring). Emits ROOT-TAGGED RELATIVE paths ([P]/[U] + path) with a one-line
    /// root legend instead of full paths: the pack root repeats ~60 identical characters per entry,
    /// which is pure token waste for the model. Unfiltered output is capped hard at 40 entries (with
    /// a "(N more — pass a filter)" nudge) because a broad listing is a browse, not a lookup.</summary>
    public Task<string> ListSamplesAsync(string? filter, CancellationToken ct = default)
    {
        var roots = SampleRoots();
        int cap = string.IsNullOrEmpty(filter) ? 40 : 150;
        const int scanMax = 2000;  // bound the match count so a huge library can't stall the call
        var hits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        bool truncatedScan = false;
        foreach (var (root, tag) in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;
                    if (Array.IndexOf(SampleExts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                    if (!string.IsNullOrEmpty(filter) && f.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    total++;
                    if (hits.Count < cap) hits.Add(tag + Path.GetRelativePath(root, f));
                    else if (total >= scanMax) { truncatedScan = true; break; }
                }
            }
            catch { /* skip inaccessible trees, keep what we found */ }
            if (truncatedScan) break;
        }
        if (hits.Count == 0) return Task.FromResult(string.IsNullOrEmpty(filter) ? "(no samples found)" : $"(no samples matching '{filter}')");
        string legend = string.Join(" ", roots.Where(r => Directory.Exists(r.Root)).Select(r => $"{r.Tag}={r.Root}"));
        string head = $"{hits.Count} of {total}{(truncatedScan ? "+" : "")} samples{(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")}. Roots: {legend}. Pass an entry verbatim to native_add_sample_channel.\n";
        string more = total > hits.Count ? $"\n({total - hits.Count}{(truncatedScan ? "+" : "")} more — pass a filter)" : "";
        return Task.FromResult(head + string.Join("\n", hits) + more);
    }

    /// <summary>Adds a new channel that plays the given audio sample file; returns its index.
    /// Accepts full paths or the root-tagged relative paths <see cref="ListSamplesAsync"/> emits.</summary>
    public async Task<int> AddSampleChannelAsync(string samplePath, CancellationToken ct = default)
    {
        samplePath = ResolveSamplePath(samplePath);
        if (!File.Exists(samplePath)) throw new InvalidOperationException($"Sample file not found: {samplePath}");
        int before = await GetChannelCountAsync(ct);
        ulong ch = await CallAsync("f215e0", new ulong[] { (uint)before, 0, 0 }, ct);  // FLcr_InsertChannel (default Sampler)
        if (ch == 0) throw new InvalidOperationException("Could not insert a channel.");
        await LoadIntoChannelAsync(ch, samplePath, 1, ct);  // mode 1 = load sample
        await RefreshRackAsync(ct);
        return before;
    }

    /// <summary>Replaces an existing channel's sample with a new audio file. Accepts full paths or
    /// the root-tagged relative paths <see cref="ListSamplesAsync"/> emits.</summary>
    public async Task ReplaceChannelSampleAsync(int channel, string samplePath, CancellationToken ct = default)
    {
        samplePath = ResolveSamplePath(samplePath);
        if (!File.Exists(samplePath)) throw new InvalidOperationException($"Sample file not found: {samplePath}");
        ulong ch = await ChannelObjAsync(channel, ct);
        if (ch == 0) throw new InvalidOperationException($"Channel {channel} not found.");
        await LoadIntoChannelAsync(ch, samplePath, 1, ct);
        await RefreshRackAsync(ct);
    }

    // ---- plugin parameters ----
    // Resolve a plugin instance + its param command base. slot < 0 => channel generator, else mixer FX slot.
    //   instance = (*(*(*(obj+0x38)+0x48)+0x20))(host);  count = *(int*)(obj+0x68)
    //   cmd base = channel: *(int*)(obj+0x9c)+0x8000 ; mixer: ((track*0x40+slot)<<16)+0x70008000
    private async Task<(ulong inst, int count, uint cmdBase)> ResolvePluginAsync(int channelOrTrack, int slot, CancellationToken ct)
    {
        ulong obj; uint cmdBase;
        if (slot < 0)
        {
            obj = await ChannelObjAsync(channelOrTrack, ct);
            if (obj == 0) return (0, 0, 0);
            int recEvt = await AI32Async(obj + 0x9c, ct);
            cmdBase = unchecked((uint)(recEvt + 0x8000));
        }
        else
        {
            obj = await MixerSlotObjAsync(channelOrTrack, slot, ct);
            cmdBase = unchecked((uint)(((channelOrTrack * 0x40 + slot) << 16) + 0x70008000));
        }
        if (obj == 0) return (0, 0, cmdBase);
        int count = await AI32Async(obj + 0x68, ct);
        ulong p38 = await APtrAsync(obj + 0x38, ct);
        if (p38 == 0) return (0, count, cmdBase);
        ulong host = p38 + 0x48;
        ulong inst = await CallVtblAsync(host, 0x20, "Plugin getInstance", new[] { host }, ct);
        return (inst, count, cmdBase);
    }

    /// <summary>Shared plugin-text call: both param NAME (mode 0) and param VALUE DISPLAY (mode 1) go
    /// through the SAME vtable slot (*(*inst+0x20))(inst, mode, paramIdx, rawValue, buf) on the shared
    /// TBaseAudioPlugin base, so it is flavor-agnostic: native / VST2 / VST3 alike.
    /// NB: no EnsureInModule here — for a hosted VST (e.g. Serum 2) the plugin-instance methods
    /// legitimately live in the PLUGIN's own DLL, not FLEngine. The native callabs is SEH-guarded,
    /// so a bad pointer returns ok:0 (a clean exception) rather than crashing FL.</summary>
    private async Task<string> ReadPluginTextAsync(ulong inst, uint mode, int paramIndex, uint rawValue, ulong charBuf, string what, CancellationToken ct)
    {
        ulong vti = await APtrAsync(inst, ct);
        ulong fn = await APtrAsync(vti + 0x20, ct);
        if (fn == 0) throw new InvalidOperationException($"Plugin {what} pointer is null.");
        await PokeAbsAsync(charBuf, new byte[64], ct);
        await CallAbsAsync(fn, new ulong[] { inst, mode, (uint)paramIndex, rawValue, charBuf }, ct);
        return DecodePluginText(await PeekAbsAsync(charBuf, 64, ct));
    }

    /// <summary>Decode the NUL-terminated text a plugin wrote into the scratch char buffer, stripping the
    /// leading FL "^b^a" formatting codes (bytes &lt; 0x20). "" when the plugin supplied nothing.</summary>
    private static string DecodePluginText(byte[] raw)
    {
        int end = Array.IndexOf(raw, (byte)0); if (end < 0) end = raw.Length;
        int start = 0; while (start < end && raw[start] < 0x20) start++;
        return end > start ? Encoding.ASCII.GetString(raw, start, end - start) : "";
    }

    private async Task<string> ReadParamNameAsync(ulong inst, int i, ulong charBuf, CancellationToken ct)
    {
        string s = await ReadPluginTextAsync(inst, 0, i, 0, charBuf, "getParamName", ct);  // GetParamName(?, i, ?, buf)
        return s.Length > 0 ? s : $"param {i}";
    }

    /// <summary>Reads a plugin param's current raw native value: getParamValue = (*(*inst+0x30))(inst,i,0,2) returns it in RAX.</summary>
    private async Task<int> ReadParamValueAsync(ulong inst, int i, CancellationToken ct)
    {
        ulong vti = await APtrAsync(inst, ct);
        ulong valFn = await APtrAsync(vti + 0x30, ct);
        if (valFn == 0) throw new InvalidOperationException("Plugin getParamValue pointer is null."); // plugin-module ptr; callabs is SEH-guarded
        ulong rax = await CallAbsAsync(valFn, new ulong[] { inst, (uint)i, 0, 2 }, ct);
        return unchecked((int)rax);
    }

    /// <summary>Reads a param's human-readable display string (with units), the sound-design feedback
    /// loop — e.g. "1.2 kHz", "-6.0 dB", "62 %". Mode 1 = value display. Returns "" if the plugin supplies none.</summary>
    private async Task<string> ReadParamValueStringAsync(ulong inst, int i, int raw, ulong charBuf, CancellationToken ct)
        => (await ReadPluginTextAsync(inst, 1, i, unchecked((uint)raw), charBuf, "getParamValueString", ct)).Trim();

    /// <summary>Lists a plugin's parameters as "index: name" (slot &lt; 0 = channel generator). Optional name filter.</summary>
    public async Task<string> ListPluginParamsAsync(int channelOrTrack, int slot, string? filter, CancellationToken ct = default)
    {
        var (inst, count, _) = await ResolvePluginAsync(channelOrTrack, slot, ct);
        if (inst == 0) return "No plugin found (the slot is empty or the channel has no generator).";
        if (count <= 0) return "This plugin exposes no parameters.";
        ulong charBuf = await ScratchAsync(ct) + 0x40;
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < count && shown < 200; i++)
        {
            string name = await ReadParamNameAsync(inst, i, charBuf, ct);
            if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            int val = await ReadParamValueAsync(inst, i, ct);
            // Prefer the plugin's own display string (units) so the LLM can reason about real targets
            // ("Cutoff = 1.2 kHz"). Fallback when a plugin gives none: VST params return their raw value
            // as normalized float bits, so reinterpret to a 0..1 figure rather than print a huge integer.
            string disp = await ReadParamValueStringAsync(inst, i, val, charBuf, ct);
            if (disp.Length == 0)
            {
                float f = BitConverter.Int32BitsToSingle(val);
                disp = float.IsFinite(f) && Math.Abs(f) <= 1e6f ? f.ToString("0.###") : val.ToString();
            }
            sb.Append(i).Append(": ").Append(name).Append(" = ").Append(disp).Append('\n');
            shown++;
        }
        if (shown == 0) return $"No parameters match '{filter}' ({count} total).";
        string head = string.IsNullOrEmpty(filter)
            ? $"{count} parameters{(shown < count ? " (showing first 200; use a name filter)" : "")}:\n"
            : $"{count} parameters, {shown} matching '{filter}':\n";
        return head + sb.ToString().TrimEnd();
    }

    /// <summary>Sets a plugin parameter to a normalized value 0..1 (slot &lt; 0 = channel generator).</summary>
    public async Task SetPluginParamAsync(int channelOrTrack, int slot, int paramIndex, double value, CancellationToken ct = default)
    {
        var (inst, count, cmdBase) = await ResolvePluginAsync(channelOrTrack, slot, ct);
        if (inst == 0) throw new InvalidOperationException("No plugin found (the slot is empty or the channel has no generator).");
        if (paramIndex < 0 || paramIndex >= count) throw new InvalidOperationException($"paramIndex out of range (0..{count - 1}).");
        uint fixedVal = (uint)Math.Round(Math.Clamp(value, 0.0, 1.0) * 1073741824.0);  // norm * 2^30
        await DispatchCommandAsync(cmdBase + (uint)paramIndex, fixedVal, 0x3fd, ct);
    }

    // ---- automation target links (what a clip CONTROLS) ----
    // Registry (re/11 §3): a global TList (items @+0x08, count @+0x10, capacity @+0x14) of 0x18-byte
    // entries { +0x00 targetDesc, +0x08 sourceEventId (= clip channel's +0x9c), +0x10 targetEventId }.
    // The RE note's registry global reads literally as *(0x14A81C0) — but that slot is the LIVE-VERIFIED
    // play-state holder (Transport), so the note's parse is ambiguous. Resolve DEFENSIVELY: probe each
    // plausible parse and require TList-shaped fields before trusting one. Read-only peeks throughout —
    // a wrong candidate can only fail validation (peeks are SEH-guarded), never fault FL.
    private async Task<ulong> AutomationLinkRegistryAsync(CancellationToken ct)
    {
        var candidates = new List<ulong>();
        try
        {
            ulong b8 = await GPtrAsync("14a81b8", ct);
            if (b8 > 0x10000)
            {
                candidates.Add(b8);                                // registry object at *(0x14A81B8)
                candidates.Add(await APtrAsync(b8 + 8, ct));       // TList hanging at *(reg + 8)
            }
            candidates.Add(await GPtrAsync("14a81c0", ct));        // the note's literal parse, last
        }
        catch (InvalidOperationException) { /* keep whatever candidates resolved */ }
        foreach (ulong cand in candidates)
        {
            if (cand <= 0x10000) continue;
            try
            {
                ulong items = await APtrAsync(cand + 8, ct);
                int count = await AI32Async(cand + 0x10, ct);
                int capacity = await AI32Async(cand + 0x14, ct);
                if (items > 0x10000 && count >= 0 && count <= 8192 && capacity >= count) return cand;
            }
            catch (InvalidOperationException) { /* unmapped — not this parse */ }
        }
        return 0;
    }

    /// <summary>Target event ids linked to a clip channel (entries whose +0x08 source id equals the
    /// channel's recEventId +0x9c). Null when the registry couldn't be resolved (unknown ≠ none).</summary>
    private async Task<List<int>?> AutomationTargetIdsAsync(ulong ch, CancellationToken ct)
    {
        ulong reg = await AutomationLinkRegistryAsync(ct);
        if (reg == 0) return null;
        int srcId = await AI32Async(ch + 0x9c, ct);
        ulong items = await APtrAsync(reg + 8, ct);
        int count = await AI32Async(reg + 0x10, ct);
        var result = new List<int>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                ulong entry = await APtrAsync(items + (ulong)i * 8, ct);
                if (entry <= 0x10000) continue;
                if (await AI32Async(entry + 8, ct) != srcId) continue;
                int tgt = await AI32Async(entry + 0x10, ct);
                if (!result.Contains(tgt)) result.Add(tgt);
            }
            catch (InvalidOperationException) { break; }   // garbage entry chain — stop, keep what matched
        }
        return result;
    }

    /// <summary>Human name for an FL event/param id via FLgl_cmd_GetEventIDName (Delphi hidden-out-param
    /// call, same recipe as GetArrangementName). "" when FL supplies none or the call faults.</summary>
    private async Task<string> EventIdNameAsync(int eventId, CancellationToken ct)
    {
        try
        {
            ulong outSlot = await ZeroedScratchSlotAsync(0x380, ct);
            await CallAsync("f5ca00", new ulong[] { outSlot, (uint)eventId, 0 }, ct);
            string s = await ReadDelphiStringAsync(await APtrAsync(outSlot, ct), ct);
            return s.Any(char.IsControl) ? "" : s;
        }
        catch (InvalidOperationException) { return ""; }
    }

    /// <summary>"name, name" of everything a clip channel automates; "" = registry says no links;
    /// null = registry unresolved (report nothing rather than a false "no target").</summary>
    private async Task<string?> DescribeAutomationTargetsAsync(ulong ch, CancellationToken ct)
    {
        try
        {
            var ids = await AutomationTargetIdsAsync(ch, ct);
            if (ids is null) return null;
            if (ids.Count == 0) return "";
            var names = new List<string>();
            foreach (int id in ids.Take(8))
            {
                string nm = await EventIdNameAsync(id, ct);
                names.Add(nm.Length > 0 ? nm : $"event 0x{id:x}");
            }
            return string.Join(", ", names);
        }
        catch (InvalidOperationException) { return null; }
    }

    // ---- automation clips ----
    // channel -> container *(ch+0x390) -> env *(cont+0x10) -> points dynarray *(env+0x28) (0x20-byte
    // elements: deltaTime f32@0, value f32@4, tension f32@8, curve u8@0xc; count @arr-8). Times are in
    // beats (default 2nd point at 4.0 = one 4/4 bar). Make one with native_add_channel("Automation Clip").
    private async Task<ulong> AutomationEnvAsync(int channel, CancellationToken ct)
    {
        ulong ch = await ChannelObjAsync(channel, ct);
        if (ch == 0) return 0;
        ulong cont = await APtrAsync(ch + 0x390, ct);
        return cont == 0 ? 0 : await APtrAsync(cont + 0x10, ct);
    }

    public async Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default)
    {
        ulong env = await AutomationEnvAsync(channel, ct);
        if (env == 0) return $"Channel {channel} is not an automation clip.";
        // Say WHAT the clip controls up front — points without a target are meaningless to the model.
        // null (registry unresolved) omits the line; "" (resolved, no links) states it plainly.
        ulong chObj = await ChannelObjAsync(channel, ct);
        string? targets = chObj != 0 ? await DescribeAutomationTargetsAsync(chObj, ct) : null;
        string targetLine = targets switch
        {
            null => "",
            "" => "automates: nothing (no target link yet)\n",
            _ => $"automates: {targets}\n",
        };
        ulong arr = await APtrAsync(env + 0x28, ct);
        if (arr == 0) return (targetLine + "(no points)").TrimEnd();
        int n = BitConverter.ToInt32(await PeekAbsAsync(arr - 8, 4, ct), 0);
        if (n <= 0 || n > 4000) return (targetLine + "(no points)").TrimEnd();
        var sb = new StringBuilder($"{targetLine}{n} points (time in beats):\n");
        double abs = 0;
        for (int i = 0; i < n; i++)
        {
            byte[] p = await PeekAbsAsync(arr + (ulong)i * 0x20, 0x10, ct);
            abs += BitConverter.ToSingle(p, 0);
            sb.Append($"  [{i}] t={abs:0.###} value={BitConverter.ToSingle(p, 4):0.###} tension={BitConverter.ToSingle(p, 8):0.###} curve={p[0xc]}\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Add an automation point (time in beats, value 0..1, tension -1..1) — inserts in time order
    /// and rebuilds the curve (integer-only SetLength + poke + recompute; no XMM needed).</summary>
    public async Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default)
    {
        ulong env = await AutomationEnvAsync(channel, ct);
        if (env == 0) throw new InvalidOperationException($"Channel {channel} is not an automation clip.");
        ulong arr = await APtrAsync(env + 0x28, ct);
        int n = arr != 0 ? BitConverter.ToInt32(await PeekAbsAsync(arr - 8, 4, ct), 0) : 0;
        if (n < 0 || n > 4000) n = 0;
        var pts = new List<(double t, float v, float ten, byte cv)>();
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            byte[] p = await PeekAbsAsync(arr + (ulong)i * 0x20, 0x10, ct);
            acc += BitConverter.ToSingle(p, 0);
            pts.Add((acc, BitConverter.ToSingle(p, 4), BitConverter.ToSingle(p, 8), p[0xc]));
        }
        pts.Add((timeBeats, (float)Math.Clamp(value, 0, 1), (float)Math.Clamp(tension, -1, 1), 0));
        pts.Sort((x, y) => x.t.CompareTo(y.t));
        int N = pts.Count;
        await CallAsync("417fc0", new ulong[] { env + 0x28, GhidraToRuntime(0xB2C678), 1, (uint)N }, ct);  // SetLength
        ulong arr2 = await APtrAsync(env + 0x28, ct);
        double prev = 0;
        for (int i = 0; i < N; i++)
        {
            ulong p = arr2 + (ulong)i * 0x20;
            await PokeAbsAsync(p + 0, BitConverter.GetBytes((float)(pts[i].t - prev)), ct); prev = pts[i].t;
            await PokeAbsAsync(p + 4, BitConverter.GetBytes(pts[i].v), ct);
            await PokeAbsAsync(p + 8, BitConverter.GetBytes(pts[i].ten), ct);
            await PokeAbsAsync(p + 0xc, new byte[1] { pts[i].cv }, ct);
            await PokeAbsAsync(p + 0xd, new byte[0x13], ct);  // zero cached coefficients
        }
        ulong vt = await APtrAsync(env, ct);
        ulong rec = await APtrAsync(vt + 0x40, ct);
        if (IsInModule(rec)) await CallAbsAsync(rec, new ulong[] { env }, ct);  // recompute
        await RefreshRackAsync(ct);
    }

    /// <summary>Delete an automation point by index (FLac_DeletePoint handles delta-fixup + recompute).</summary>
    public async Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default)
    {
        ulong env = await AutomationEnvAsync(channel, ct);
        if (env == 0) throw new InvalidOperationException($"Channel {channel} is not an automation clip.");
        await CallAsync("b30ad0", new ulong[] { env, (uint)index }, ct);  // FLac_DeletePoint
        await RefreshRackAsync(ct);
    }
}
