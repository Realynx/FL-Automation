using System.ComponentModel;
using FruityLink.Agent.Versioning;
using FruityLink.Core.Abstractions;
using Microsoft.SemanticKernel;
using static FruityLink.Agent.Plugins.BulkArgs;
using static FruityLink.Agent.Plugins.PluginSupport;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Direct native control of FL Studio through the injected DLL bridge (command-bus param protocol).
/// These tools execute natively inside FL and cover the master/mixer/channel parameter surface —
/// more powerful than, and complementary to, the MIDI-script tools. Values use FL's native integer
/// scales (see each parameter description).
/// Every tool returns the shared "OK: …"/"ERR: …" envelope (<see cref="PluginSupport"/>) so weak
/// backends parse every result the same way. Descriptions are deliberately terse: the system prompt
/// states the shared PPQ/MIDI conventions ONCE, so tools don't restate them.
/// </summary>
public sealed class NativeControlPlugin(INativeFlControl fl, ChangeCapture? capture = null)
{
    // The read-before-write seam every Phase-1 mutating tool routes through. Inert (no journaling) when
    // no capture is supplied — then the tools behave exactly as before. One instance, one code path.
    private readonly ChangeCapture _capture = capture ?? ChangeCapture.Disabled(fl);
    // Intentionally NOT a [KernelFunction] (tool-surface prune): every native_* tool already returns
    // a clear ERR when the bridge is down, so a dedicated probe only invited warm-up calls that
    // burned a round-trip. Kept public — the UI/probe check bridge health through it explicitly.
    public Task<string> IsAvailableAsync(CancellationToken ct = default) => Run(async () =>
        await fl.IsAvailableAsync(ct) ? Ok("native bridge available") : Err("native bridge NOT injected"));

    // Multi-version tool gating (infrastructure — NOT a [KernelFunction], never advertised to the model):
    // exposes the injected bridge's signature-scan status so the kernel builder can hide native tools
    // whose required FL symbol didn't resolve on the running FL version. Returns null when the wrapped
    // control can't report it (mocks/tests) or the bridge isn't ready yet → the builder then gates
    // nothing (fail open). See NativeSymbolGate + AgentKernelBuilder.
    internal Task<FlSymbolStatus?> GetSymbolStatusAsync(CancellationToken ct = default) =>
        fl is IFlSymbolResolution resolver
            ? resolver.GetSymbolStatusAsync(ct)
            : Task.FromResult<FlSymbolStatus?>(null);

    [KernelFunction("native_set_tempo")]
    [Description("Set tempo, BPM 10-522.")]
    public Task<string> SetTempoAsync([Description("BPM")] double bpm, CancellationToken ct = default) => Run(async () =>
    {
        double v = Math.Clamp(bpm, 10.0, 522.0);
        await _capture.ScalarAsync(InverseOps.Tempo, JournalDict.Empty, JournalDict.Of("value", v),
            c => fl.SetTempoAsync(v, c), ct);
        return ClampReport("tempo", bpm, v, 10.0, 522.0);
    });

    [KernelFunction("native_get_tempo")]
    [Description("Read tempo (BPM).")]
    public Task<string> GetTempoAsync(CancellationToken ct = default) => Run(async () =>
        Ok($"{await fl.GetTempoAsync(ct):0.##} BPM"));

    // Intentionally NOT a [KernelFunction] (tool-surface prune): native_set_mixer_volume with
    // track 0 IS the master fader (same underlying command id), so this duplicate entry cost schema
    // tokens and split the model's choice between two identical actions. Kept public for the UI/probe.
    public Task<string> SetMasterVolumeAsync(int value, CancellationToken ct = default) => Run(async () =>
    {
        int v = Math.Clamp(value, 0, 12800);
        await fl.SetMasterVolumeAsync(v, ct);
        return ClampReport("master vol", value, v, 0, 12800);
    });

    [KernelFunction("native_set_master_pitch")]
    [Description("Set master pitch, cents -1200..1200.")]
    public Task<string> SetMasterPitchAsync([Description("Cents, -1200..1200")] int cents, CancellationToken ct = default) => Run(async () =>
    {
        int c = Math.Clamp(cents, -1200, 1200);
        await _capture.ScalarAsync(InverseOps.MasterPitch, JournalDict.Empty, JournalDict.Of("value", c),
            k => fl.SetMasterPitchAsync(c, k), ct);
        return ClampReport("master pitch(cents)", cents, c, -1200, 1200);
    });

    [KernelFunction("native_set_shuffle")]
    [Description("Set global shuffle/swing, 0-128.")]
    public Task<string> SetShuffleAsync([Description("0-128")] int value, CancellationToken ct = default) => Run(async () =>
    {
        int v = Math.Clamp(value, 0, 128);
        await _capture.ScalarAsync(InverseOps.Shuffle, JournalDict.Empty, JournalDict.Of("value", v),
            c => fl.SetShuffleAsync(v, c), ct);
        return ClampReport("shuffle", value, v, 0, 128);
    });

    [KernelFunction("native_set_mixer_volume")]
    [Description("Set mixer track volume 0-12800 (~7624 = 0 dB); track 0 = master.")]
    public Task<string> SetMixerVolumeAsync(
        [Description("Mixer track 0-125 (0 = master)")] int track,
        [Description("0-12800 (~7624 = 0 dB)")] int value, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } e) return e;
        int v = Math.Clamp(value, 0, 12800);
        await _capture.ScalarAsync(InverseOps.MixerVolume, JournalDict.Of("track", track), JournalDict.Of("value", v),
            c => fl.SetMixerVolumeAsync(track, v, c), ct);
        return ClampReport($"mixer {track} vol", value, v, 0, 12800);
    });

    [KernelFunction("native_set_mixer_pan")]
    [Description("Set mixer track pan 0-12800 (6400 = center).")]
    public Task<string> SetMixerPanAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("0-12800, 6400 = center")] int value, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } e) return e;
        int v = Math.Clamp(value, 0, 12800);
        await _capture.ScalarAsync(InverseOps.MixerPan, JournalDict.Of("track", track), JournalDict.Of("value", v),
            c => fl.SetMixerPanAsync(track, v, c), ct);
        return ClampReport($"mixer {track} pan", value, v, 0, 12800);
    });

    // NOTE: intentionally NOT a [KernelFunction] — it took a raw "normalized fixed-point" long with no
    // stated range, which the model could not use correctly and confused with native_set_mixer_plugin_param
    // (normalized 0.0-1.0). Kept as a public method; the LLM-facing setter is native_set_mixer_plugin_param.
    public Task<string> SetMixerFxParamAsync(
        int track, int slot, int paramIndex, long value, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SetMixerFxParamAsync(track, slot, paramIndex, value, ct);
        return Ok($"mixer {track} slot {slot} param {paramIndex} set");
    });

    [KernelFunction("native_set_channel_volume")]
    [Description("Set channel volume 0-12800 (10000 = default).")]
    public Task<string> SetChannelVolumeAsync(
        [Description("Channel index")] int channel,
        [Description("0-12800 (10000 = default)")] int value, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        int v = Math.Clamp(value, 0, 12800);
        await _capture.ScalarAsync(InverseOps.ChannelVolume, JournalDict.Of("channel", channel), JournalDict.Of("value", v),
            c => fl.SetChannelVolumeAsync(channel, v, c), ct);
        return ClampReport($"chan {channel} vol", value, v, 0, 12800);
    });

    [KernelFunction("native_set_channel_pan")]
    [Description("Set channel pan 0-12800 (6400 = center).")]
    public Task<string> SetChannelPanAsync(
        [Description("Channel index")] int channel,
        [Description("0-12800, 6400 = center")] int value, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        int v = Math.Clamp(value, 0, 12800);
        await _capture.ScalarAsync(InverseOps.ChannelPan, JournalDict.Of("channel", channel), JournalDict.Of("value", v),
            c => fl.SetChannelPanAsync(channel, v, c), ct);
        return ClampReport($"chan {channel} pan", value, v, 0, 12800);
    });

    [KernelFunction("native_set_channel_pitch")]
    [Description("Set channel pitch in cents (0 = center).")]
    public Task<string> SetChannelPitchAsync(
        [Description("Channel index")] int channel,
        [Description("Cents (0 = center)")] int cents, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        await _capture.ScalarAsync(InverseOps.ChannelPitch, JournalDict.Of("channel", channel), JournalDict.Of("value", cents),
            c => fl.SetChannelPitchAsync(channel, cents, c), ct);
        return Ok($"chan {channel} pitch={cents} cents");
    });

    [KernelFunction("native_set_channel_muted")]
    [Description("Mute/unmute a channel.")]
    public Task<string> SetChannelMutedAsync(
        [Description("Channel index")] int channel,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        await _capture.ScalarAsync(InverseOps.ChannelMuted, JournalDict.Of("channel", channel), JournalDict.Of("value", muted),
            c => fl.SetChannelMutedAsync(channel, muted, c), ct);
        return Ok($"chan {channel} {(muted ? "muted" : "unmuted")}");
    });

    /// <summary>Resolves a channel reference — an integer index OR a channel NAME (exact case-insensitive,
    /// then a unique case-insensitive substring) — to its index, so write tools accept "Bass" as well as "3"
    /// and the model can skip the native_list_channels index lookup (a read-before-write round-trip). Returns
    /// (index, error): a non-null error is a ready-to-return tool ERR.</summary>
    private async Task<(int index, string? error)> ResolveChannelAsync(string reference, CancellationToken ct)
    {
        string r = (reference ?? string.Empty).Trim();
        if (r.Length == 0) return (-1, Err("no channel given — pass a channel index or name"));
        if (int.TryParse(r, out int idx))
            return Guard("channel", idx, 0, int.MaxValue) is { } g ? (-1, g) : (idx, null);

        int count = await fl.GetChannelCountAsync(ct);
        var exact = new List<int>();
        var partial = new List<int>();
        for (int i = 0; i < count; i++)
        {
            string name = await fl.GetChannelNameAsync(i, ct);
            if (string.Equals(name, r, StringComparison.OrdinalIgnoreCase)) exact.Add(i);
            else if (name.Contains(r, StringComparison.OrdinalIgnoreCase)) partial.Add(i);
        }
        if (exact.Count == 1) return (exact[0], null);
        if (exact.Count > 1) return (-1, Err($"channel name '{r}' is ambiguous ({exact.Count} matches) — use its index from native_list_channels"));
        if (partial.Count == 1) return (partial[0], null);
        if (partial.Count > 1) return (-1, Err($"'{r}' matches {partial.Count} channels — be more specific or use its index"));
        return (-1, Err($"no channel named '{r}' — use native_list_channels to see channel names/indices"));
    }

    [KernelFunction("native_route_channel_to_mixer")]
    [Description("Route a channel (index OR name, e.g. 3 or 'Bass') to a mixer track 0-125.")]
    public Task<string> SetChannelFxRouteAsync(
        [Description("Channel index or name")] string channel,
        [Description("Mixer track 0-125")] int mixerTrack, CancellationToken ct = default) => Run(async () =>
    {
        var (ch, chErr) = await ResolveChannelAsync(channel, ct);
        if (chErr is not null) return chErr;
        if (Guard("mixer track", mixerTrack, 0, 125) is { } me) return me;
        await _capture.ScalarAsync(InverseOps.ChannelRoute, JournalDict.Of("channel", ch), JournalDict.Of("value", mixerTrack),
            c => fl.SetChannelFxRouteAsync(ch, mixerTrack, c), ct);
        return Ok($"chan {ch} -> mixer {mixerTrack}");
    });

    [KernelFunction("native_add_note")]
    [Description("Add ONE piano-roll note. For 2+ notes use native_add_notes (one call, far faster).")]
    public Task<string> AddNoteAsync(
        [Description("Pattern 1-based; 0 or -1 = current")] int pattern,
        [Description("Channel index (instrument)")] int channel,
        [Description("MIDI key 0-131")] int key,
        [Description("Start tick")] int startTick,
        [Description("Length, ticks")] int lengthTick,
        [Description("Velocity 0-127 (100 = default)")] int velocity = 100,
        CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        await fl.AddNoteAsync(pattern, channel, key, startTick, lengthTick, velocity, ct);
        return Ok($"note key={key} @{startTick} len={lengthTick} vel={velocity} pat {(pattern <= 0 ? "current" : pattern.ToString())} chan {channel}");
    });

    [KernelFunction("native_add_notes")]
    [Description("Add MANY piano-roll notes in ONE call (preferred over native_add_note). notes: entries sep by ';' or newline, each 'key,start,length[,velocity=100[,channel]]' — e.g. '60,0,480; 64,0,480,90'. A note's 5th field overrides the channel arg.")]
    public Task<string> AddNotesAsync(
        [Description("Pattern 1-based; 0 or -1 = current")] int pattern,
        [Description("Default channel (index or name) for notes lacking a 5th field")] string channel,
        [Description("Entries 'key,start,length[,velocity[,channel]]' sep by ';' or newline")] string notes,
        CancellationToken ct = default) => Run(async () =>
    {
        var (channelIdx, chErr) = await ResolveChannelAsync(channel, ct);
        if (chErr is not null) return chErr;
        var (parsed, skipped) = ParseNotes(notes, channelIdx);
        if (parsed.Count == 0)
            return skipped.Count > 0
                ? Err($"no valid notes — {skipped.Count} malformed (e.g. '{skipped[0]}'). Use 'key,start,length[,velocity[,channel]]' with numbers")
                : Err("no notes parsed — provide notes like '60,0,480,100; 64,480,480,100'");
        await fl.AddNotesAsync(pattern, parsed, ct);
        string where = pattern <= 0 ? "current" : pattern.ToString();
        return skipped.Count == 0
            ? Ok($"{parsed.Count} note(s) -> pattern {where}")
            : Ok($"{parsed.Count} note(s) -> pattern {where}; skipped {skipped.Count} malformed (e.g. '{skipped[0]}')");
    });

    [KernelFunction("native_get_ppq")]
    [Description("Get timebase: ticks per quarter note (PPQ).")]
    public Task<string> GetPpqAsync(CancellationToken ct = default) => Run(async () =>
        Ok($"{await fl.GetPpqAsync(ct)} ticks per quarter note"));

    // ---------------- Patterns ----------------

    [KernelFunction("native_list_patterns")]
    [Description("List patterns with content as 'index: name [length ticks/bars, notes]', marking current.")]
    public Task<string> ListPatternsAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListPatternsAsync(ct)));

    [KernelFunction("native_get_current_pattern")]
    [Description("Get current (selected) pattern number.")]
    public Task<string> GetCurrentPatternAsync(CancellationToken ct = default) => Run(async () =>
        Ok($"current pattern: {await fl.GetCurrentPatternAsync(ct)}"));

    [KernelFunction("native_select_pattern")]
    [Description("Select a pattern (1-based) in the UI. NOT needed before note/clip tools — they take an explicit pattern.")]
    public Task<string> SelectPatternAsync([Description("Pattern 1-based")] int index, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SelectPatternAsync(index, ct);
        return Ok($"pattern {index} selected");
    });

    [KernelFunction("native_create_pattern")]
    [Description("Usually unneeded — native_add_notes(pattern=N) realizes pattern N directly. Pass index to SELECT a specific number (e.g. 12); omit/0 = first empty. Targeting a number is collision-safe in parallel tasks, unlike 'next empty'.")]
    public Task<string> CreatePatternAsync(
        [Description("Target pattern number 1-based; 0 = next empty")] int index = 0,
        CancellationToken ct = default) => Run(async () =>
    {
        if (index > 0)
        {
            await fl.SelectPatternAsync(index, ct);
            return Ok($"pattern {index} selected — add its notes now");
        }
        return Ok($"pattern {await fl.CreatePatternAsync(ct)} selected (empty — add its notes now; calling native_create_pattern again before you do returns this same number)");
    });

    [KernelFunction("native_clear_pattern")]
    [Description("Delete all notes in a pattern (1-based).")]
    public Task<string> ClearPatternAsync([Description("Pattern 1-based")] int index, CancellationToken ct = default) => Run(async () =>
    {
        await fl.ClearPatternAsync(index, ct);
        return Ok($"pattern {index} cleared");
    });

    [KernelFunction("native_set_pattern_name")]
    [Description("Rename a pattern (1-based; persists) — label parts like 'Verse'/'Drop' instead of 'Pattern N'.")]
    public Task<string> SetPatternNameAsync(
        [Description("Pattern 1-based")] int index,
        [Description("New name")] string name, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SetPatternNameAsync(index, name ?? string.Empty, ct);
        return Ok($"pattern {index} = '{name}'");
    });

    // ---------------- Channels ----------------

    [KernelFunction("native_list_channels")]
    [Description("List channels with state (mixer route, mute, non-default vol/pan) as 'index: name …' — map instrument names to the index other tools need.")]
    public Task<string> ListChannelsAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListChannelsAsync(ct)));

    [KernelFunction("native_select_channel")]
    [Description("Select a channel (0-based) in the UI. NOT needed before note tools — they take an explicit channel.")]
    public Task<string> SelectChannelAsync([Description("Channel 0-based")] int index, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SelectChannelAsync(index, ct);
        return Ok($"channel {index} selected");
    });

    [KernelFunction("native_set_channel_name")]
    [Description("Rename a channel (persists). Do this after adding a channel so you can find it by name later with native_list_channels.")]
    public Task<string> SetChannelNameAsync(
        [Description("Channel 0-based")] int index,
        [Description("New name")] string name, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", index, 0, int.MaxValue) is { } e) return e;
        await fl.SetChannelNameAsync(index, name ?? string.Empty, ct);
        return Ok($"channel {index} = '{name}'");
    });

    [KernelFunction("native_solo_channel")]
    [Description("Toggle exclusive SOLO on a channel (call again to un-solo) — hear one part without muting every other channel by hand.")]
    public Task<string> SoloChannelAsync([Description("Channel 0-based")] int index, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", index, 0, int.MaxValue) is { } e) return e;
        await fl.SetChannelSoloAsync(index, ct);
        return Ok($"toggled solo on channel {index}");
    });

    // ---------------- Mixer (sends / EQ) ----------------

    [KernelFunction("native_list_mixer_tracks")]
    [Description("List NAMED mixer tracks as 'index: name' to map a bus/track NAME to the index mixer tools need " +
                 "(0=Master, 1-125=Inserts, 126=Current). Call FIRST when the user names a bus instead of a number; " +
                 "don't scan channels.")]
    public Task<string> ListMixerTracksAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListMixerTracksAsync(ct)));

    [KernelFunction("native_set_mixer_track_name")]
    [Description("Rename a mixer track/bus (persists) so a bus you create is resolvable by name later with native_list_mixer_tracks. Empty name resets to the default (Insert N / Master).")]
    public Task<string> SetMixerTrackNameAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("New name; empty = default")] string name, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } e) return e;
        await fl.SetMixerTrackNameAsync(track, name ?? string.Empty, ct);
        return Ok($"mixer {track} = '{name}'");
    });

    [KernelFunction("native_set_mixer_track_muted")]
    [Description("Mute/unmute a mixer track.")]
    public Task<string> SetMixerTrackMutedAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } e) return e;
        await _capture.ScalarAsync(InverseOps.MixerTrackMuted, JournalDict.Of("track", track), JournalDict.Of("value", muted),
            c => fl.SetMixerTrackMutedAsync(track, muted, c), ct);
        return Ok($"mixer {track} {(muted ? "muted" : "unmuted")}");
    });

    [KernelFunction("native_set_mixer_send")]
    [Description("Route + set mixer send srcTrack->dstTrack, level 0.0-1.25 (1.0 = unity).")]
    public Task<string> SetMixerSendAsync(
        [Description("Source mixer track 0-125")] int srcTrack,
        [Description("Dest mixer track 0-125")] int dstTrack,
        [Description("Send level 0.0-1.25 (1.0 = unity)")] double level, CancellationToken ct = default) => Run(async () =>
    {
        // Guard BOTH indices and clamp the level BEFORE the bridge call: the send level is poked
        // straight into FL's routing matrix memory, so out-of-bounds values would write raw FL memory
        // at a wrong slot address / store a garbage gain.
        if (Guard("srcTrack", srcTrack, 0, 125) is { } se) return se;
        if (Guard("dstTrack", dstTrack, 0, 125) is { } de) return de;
        double v = Math.Clamp(level, 0.0, 1.25);
        await fl.SetMixerSendAsync(srcTrack, dstTrack, v, ct);
        return ClampReport($"send {srcTrack}->{dstTrack}", level, v, 0.0, 1.25);
    });

    [KernelFunction("native_set_mixer_eq_gain")]
    [Description("Set mixer track EQ band gain in dB. band: 0=low, 1=mid, 2=high.")]
    public Task<string> SetMixerEqGainAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("EQ band: 0=low, 1=mid, 2=high")] int band,
        [Description("Gain in dB, -18..+18 (0 = flat)")] double gainDb, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } te) return te;
        if (Guard("EQ band", band, 0, 2) is { } be) return be;
        double db = Math.Clamp(gainDb, -18.0, 18.0);
        // Model-facing unit is dB; FL stores the band gain as raw fixed-point 0..2^30 with the
        // midpoint 0x20000000 = 0 dB and a ±18 dB fader range mapped linearly onto it:
        // raw = (dB + 18) / 36 * 2^30. Exposing the raw scale made the model guess huge integers.
        int raw = (int)Math.Round((db + 18.0) / 36.0 * 1073741824.0);
        await fl.SetMixerEqGainAsync(track, band, raw, ct);
        return ClampReport($"mixer {track} EQ band {band} dB", gainDb, db, -18.0, 18.0);
    });

    // ---------------- Transport ----------------

    [KernelFunction("native_transport_play")]
    [Description("Start playback.")]
    public Task<string> TransportPlayAsync(CancellationToken ct = default) => Run(async () =>
    {
        await fl.TransportPlayAsync(ct);
        return Ok("playing");
    });

    [KernelFunction("native_transport_stop")]
    [Description("Stop playback.")]
    public Task<string> TransportStopAsync(CancellationToken ct = default) => Run(async () =>
    {
        await fl.TransportStopAsync(ct);
        return Ok("stopped");
    });

    [KernelFunction("native_transport_toggle_record")]
    [Description("Toggle record-arm.")]
    public Task<string> TransportToggleRecordAsync(CancellationToken ct = default) => Run(async () =>
    {
        await fl.TransportToggleRecordAsync(ct);
        return Ok("record toggled");
    });

    [KernelFunction("native_set_loop_region")]
    [Description("Set the song loop region to [startTick, endTick] (PPQ ticks) so the transport loops just that span — e.g. loop the drop while editing. Pass endTick < 0 (or <= startTick) to CLEAR the loop.")]
    public Task<string> SetLoopRegionAsync(
        [Description("Loop start tick")] int startTick,
        [Description("Loop end tick; < 0 or <= start clears the loop")] int endTick,
        CancellationToken ct = default) => Run(async () =>
    {
        await fl.SetLoopRegionAsync(startTick, endTick, ct);
        return Ok(endTick < 0 || endTick <= startTick ? "loop cleared" : $"loop [{Math.Max(0, startTick)}..{endTick}]");
    });

    // ---------------- Plugins / inserts ----------------

    [KernelFunction("native_list_available_plugins")]
    [Description("List installed plugin names (max 150 shown). kind: 'generator' (channel instruments) or 'effect' (mixer FX). Use names with native_add_channel / native_add_mixer_effect.")]
    public Task<string> ListAvailablePluginsAsync(
        [Description("'generator' or 'effect'")] string kind,
        [Description("Optional name filter; empty = all")] string filter = "",
        CancellationToken ct = default) => Run(async () =>
    {
        // Strict kind validation: a typo'd kind silently listing generators made the model load
        // instruments as effects. Anything that isn't clearly one of the two kinds is an ERR.
        string k = (kind ?? string.Empty).Trim().ToLowerInvariant();
        bool effects = k is "effect" or "effects" or "fx";
        bool generators = k is "generator" or "generators" or "instrument" or "instruments";
        if (!effects && !generators) return Err($"kind '{kind}' invalid — use 'generator' or 'effect'");

        string raw = await fl.ListAvailablePluginsAsync(effects, ct);
        if (raw.StartsWith('(')) return Ok(raw);  // "(none found)" / "(plugin database not found …)"
        var names = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => string.IsNullOrWhiteSpace(filter) || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (names.Count == 0) return Ok($"no {k} plugins match '{filter}'");
        const int cap = 150;
        string more = names.Count > cap ? $"\n({names.Count - cap} more — pass a filter)" : string.Empty;
        return Ok($"{names.Count} {(effects ? "effect" : "generator")} plugins:\n{string.Join("\n", names.Take(cap))}{more}");
    });

    [KernelFunction("native_get_channel_plugin")]
    [Description("Report a channel's loaded generator plugin (name + param count).")]
    public Task<string> GetChannelPluginAsync(
        [Description("Channel 0-based")] int channel, CancellationToken ct = default) => Run(async () =>
        Ok(await fl.GetChannelPluginAsync(channel, ct)));

    [KernelFunction("native_add_channel")]
    [Description("Add a channel hosting the named generator; returns new channel index.")]
    public Task<string> AddChannelAsync(
        [Description("Generator name from native_list_available_plugins")] string plugin, CancellationToken ct = default) => Run(async () =>
        Ok($"channel {await fl.AddChannelAsync(plugin, ct)} = '{plugin}'"));

    [KernelFunction("native_list_mixer_effects")]
    [Description("Inspect a mixer track: FX slots, current vol/pan, mute/solo, and sends (destinations + levels).")]
    public Task<string> ListMixerEffectsAsync(
        [Description("Mixer track 0-125")] int track, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } e) return e;
        return Ok(await fl.ListMixerEffectsAsync(track, ct));
    });

    [KernelFunction("native_add_mixer_effect")]
    [Description("Load named effect into a mixer FX slot 0-9.")]
    public Task<string> AddMixerEffectAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Effect name from native_list_available_plugins")] string plugin, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } te) return te;
        if (Guard("FX slot", slot, 0, 9) is { } se) return se;
        await fl.AddMixerEffectAsync(track, slot, plugin, ct);
        return Ok($"'{plugin}' -> mixer {track} slot {slot}");
    });

    [KernelFunction("native_remove_mixer_effect")]
    [Description("Clear a mixer track's FX slot.")]
    public Task<string> RemoveMixerEffectAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("FX slot 0-9")] int slot, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } te) return te;
        if (Guard("FX slot", slot, 0, 9) is { } se) return se;
        await fl.RemoveMixerEffectAsync(track, slot, ct);
        return Ok($"mixer {track} slot {slot} cleared");
    });

    [KernelFunction("native_clone_mixer_effect")]
    [Description("Copy effect type between FX slots on one mixer track (type only, not param state).")]
    public Task<string> CloneMixerEffectAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("Source FX slot 0-9")] int fromSlot,
        [Description("Dest FX slot 0-9")] int toSlot, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } te) return te;
        if (Guard("fromSlot", fromSlot, 0, 9) is { } fe) return fe;
        if (Guard("toSlot", toSlot, 0, 9) is { } se) return se;
        await fl.CloneMixerEffectAsync(track, fromSlot, toSlot, ct);
        return Ok($"mixer {track} slot {fromSlot} -> slot {toSlot}");
    });

    // ---------------- Plugin parameters (VST / native sound design) ----------------

    [KernelFunction("native_list_channel_plugin_params")]
    [Description("List a channel generator's params as 'index: name = value'. Filter strongly recommended for big synths (Serum ~1000 params). Use index with native_set_channel_plugin_param.")]
    public Task<string> ListChannelPluginParamsAsync(
        [Description("Channel 0-based")] int channel,
        [Description("Name filter (e.g. 'cutoff'); empty = all")] string filter = "",
        CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        return Ok(await fl.ListPluginParamsAsync(channel, -1, string.IsNullOrWhiteSpace(filter) ? null : filter, ct));
    });

    [KernelFunction("native_set_channel_plugin_params")]
    [Description("Set ONE or MANY channel-generator params in ONE call. params = JSON array of {index,value}, e.g. '[{\"index\":205,\"value\":0.5}]'. value normalized 0.0-1.0; index from native_list_channel_plugin_params.")]
    public Task<string> SetChannelPluginParamsAsync(
        [Description("Channel index")] int channel,
        [Description("JSON array of {index,value}; value 0.0-1.0")] string @params,
        CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } ce) return ce;
        return await SetPluginParamsCoreAsync(channel, -1, @params,
            "[{\"index\":205,\"value\":0.5}]", $"chan {channel}", ct);
    });

    [KernelFunction("native_list_mixer_plugin_params")]
    [Description("List a mixer FX-slot effect's params as 'index: name = value'. Optional name filter. Use index with native_set_mixer_plugin_param.")]
    public Task<string> ListMixerPluginParamsAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Name filter; empty = all")] string filter = "",
        CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } te) return te;
        if (Guard("FX slot", slot, 0, 9) is { } se) return se;
        return Ok(await fl.ListPluginParamsAsync(track, slot, string.IsNullOrWhiteSpace(filter) ? null : filter, ct));
    });

    [KernelFunction("native_set_mixer_plugin_params")]
    [Description("Set ONE or MANY mixer FX-slot params in ONE call. params = a JSON array of {index,value}, e.g. '[{\"index\":3,\"value\":0.5},{\"index\":4,\"value\":0.2}]' (a single param may be a 1-element array or a bare object). value normalized 0.0-1.0; index from native_list_mixer_plugin_params.")]
    public Task<string> SetMixerPluginParamsAsync(
        [Description("Mixer track 0-125")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("JSON array of {index,value}; value 0.0-1.0")] string @params,
        CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("mixer track", track, 0, 125) is { } te) return te;
        if (Guard("FX slot", slot, 0, 9) is { } se) return se;
        return await SetPluginParamsCoreAsync(track, slot, @params,
            "[{\"index\":3,\"value\":0.5}]", $"mixer {track} slot {slot}", ct);
    });

    /// <summary>Shared core of the two Set*PluginParams tools: parse the {index,value} list, guard
    /// each param index, clamp values to 0.0-1.0, and set them one by one. <paramref name="index"/> +
    /// <paramref name="slot"/> address the plugin (channel = index with slot -1; mixer = track + FX
    /// slot); <paramref name="example"/> and <paramref name="okTarget"/> keep each tool's exact
    /// ERR/OK wording.</summary>
    private async Task<string> SetPluginParamsCoreAsync(
        int index, int slot, string @params, string example, string okTarget, CancellationToken ct)
    {
        var (items, err) = ParseParams(@params);
        if (err is not null) return Err(err);
        if (items.Count == 0) return Err($"no params parsed — provide '{example}'");
        foreach (var (paramIndex, value) in items)
        {
            if (Guard("param index", paramIndex, 0, int.MaxValue) is { } pe) return pe;
            await fl.SetPluginParamAsync(index, slot, paramIndex, Math.Clamp(value, 0.0, 1.0), ct);
        }
        return Ok($"set {items.Count} param(s) on {okTarget}");
    }

    // ---------------- Samples ----------------

    [KernelFunction("native_list_samples")]
    [Description("List audio samples (FL packs + user content) for drums/one-shots/loops. Returns root-tagged relative paths; pass an entry verbatim to native_add_sample_channel.")]
    public Task<string> ListSamplesAsync(
        [Description("Name/path filter — strongly recommended (e.g. kick)")] string filter = "",
        CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListSamplesAsync(string.IsNullOrWhiteSpace(filter) ? null : filter, ct)));

    [KernelFunction("native_add_sample_channel")]
    [Description("Add a channel playing the given audio sample; returns new channel index.")]
    public Task<string> AddSampleChannelAsync(
        [Description("Sample path — an entry from native_list_samples, or a full path")] string samplePath, CancellationToken ct = default) => Run(async () =>
        Ok($"sample channel {await fl.AddSampleChannelAsync(samplePath, ct)} = {System.IO.Path.GetFileName(samplePath)}"));

    [KernelFunction("native_replace_channel_sample")]
    [Description("Replace a channel's sample with another audio file.")]
    public Task<string> ReplaceChannelSampleAsync(
        [Description("Channel index")] int channel,
        [Description("Sample path — an entry from native_list_samples, or a full path")] string samplePath, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        await fl.ReplaceChannelSampleAsync(channel, samplePath, ct);
        return Ok($"chan {channel} sample = {System.IO.Path.GetFileName(samplePath)}");
    });

    // ---------------- Notes (read + surgical edit) ----------------

    [KernelFunction("native_get_notes")]
    [Description("Read a pattern's piano-roll notes (channel, pitch, position, length, velocity), paged. Call ONLY when you need existing notes — not before adding new ones.")]
    public Task<string> GetNotesAsync(
        [Description("Pattern 1-based; 0 or -1 = current")] int pattern = 0,
        [Description("Channel filter; -1 = all")] int channel = -1,
        [Description("Skip first N notes; the result's continuation hint gives the next offset")] int offset = 0,
        CancellationToken ct = default) => Run(async () =>
        Ok(await fl.GetNotesAsync(pattern, channel, offset, ct)));

    [KernelFunction("native_edit_notes")]
    [Description("Change existing notes in place — never clear+re-add (that wipes the pattern). edits: JSON array; locate each note by its current channel,key,pos from native_get_notes, then set any of newKey, newStart, newLength, newVelocity, muted, e.g. '[{\"channel\":0,\"key\":60,\"pos\":0,\"newKey\":62}]'.")]
    public Task<string> EditNotesAsync(
        [Description("Pattern 1-based; 0 or -1 = current")] int pattern,
        [Description("JSON array; per note channel,key,pos + new field(s)")] string edits,
        CancellationToken ct = default) => Run(async () =>
    {
        var (parsed, err) = ParseNoteEdits(edits);
        if (err is not null) return Err(err);
        if (parsed.Count == 0) return Err("no edits parsed — provide '[{\"channel\":0,\"key\":60,\"pos\":0,\"newKey\":62}]'");
        int changed = await fl.EditNotesAsync(pattern, parsed, ct);
        return changed == 0
            ? Err($"no notes matched — check channel/key/pos against native_get_notes (pattern {(pattern <= 0 ? "current" : pattern.ToString())})")
            : Ok($"edited {changed} note(s) in pattern {(pattern <= 0 ? "current" : pattern.ToString())}");
    });

    [KernelFunction("native_delete_notes")]
    [Description("Delete specific notes, keeping the rest of the pattern (surgical; native_clear_pattern wipes ALL). notes: JSON array identifying each note by channel,key,pos from native_get_notes.")]
    public Task<string> DeleteNotesAsync(
        [Description("Pattern 1-based; 0 or -1 = current")] int pattern,
        [Description("JSON array of {channel,key,pos} from native_get_notes")] string notes,
        CancellationToken ct = default) => Run(async () =>
    {
        var (parsed, err) = ParseNoteRefs(notes);
        if (err is not null) return Err(err);
        if (parsed.Count == 0) return Err("no notes parsed — provide '[{\"channel\":0,\"key\":60,\"pos\":0}]'");
        int deleted = await fl.DeleteNotesAsync(pattern, parsed, ct);
        return deleted == 0
            ? Err($"no notes matched — check channel/key/pos against native_get_notes (pattern {(pattern <= 0 ? "current" : pattern.ToString())})")
            : Ok($"deleted {deleted} note(s) from pattern {(pattern <= 0 ? "current" : pattern.ToString())}");
    });

    [KernelFunction("native_clone_pattern")]
    [Description("Duplicate a pattern's notes into a NEW empty pattern (for making a variation without recreating notes by hand); returns the new pattern number. All note detail is preserved. Add/rename the new pattern's content after.")]
    public Task<string> ClonePatternAsync(
        [Description("Source pattern 1-based; 0 or -1 = current")] int pattern = 0,
        CancellationToken ct = default) => Run(async () =>
    {
        int newIdx = await fl.ClonePatternAsync(pattern, ct);
        return newIdx == 0
            ? Err($"nothing to clone — pattern {(pattern <= 0 ? "current" : pattern.ToString())} has no notes")
            : Ok($"cloned pattern {(pattern <= 0 ? "current" : pattern.ToString())} -> new pattern {newIdx}");
    });

    // ---------------- Playlist tracks ----------------

    [KernelFunction("native_list_playlist_tracks")]
    [Description("List customized playlist tracks (name, color, mute, collapse, type); unlisted tracks are default.")]
    public Task<string> ListPlaylistTracksAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListPlaylistTracksAsync(ct)));

    [KernelFunction("native_set_track_name")]
    [Description("Rename a playlist track (1-based).")]
    public Task<string> SetTrackNameAsync(
        [Description("Playlist track 1-500")] int track,
        [Description("New name")] string name, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("track", track, 1, 500) is { } e) return e;
        await _capture.ScalarAsync(InverseOps.TrackName, JournalDict.Of("track", track), JournalDict.Of("value", name),
            c => fl.SetTrackNameAsync(track, name, c), ct);
        return Ok($"track {track} = '{name}'");
    });

    [KernelFunction("native_set_track_color")]
    [Description("Set a playlist track color, RGB hex like #FF8800.")]
    public Task<string> SetTrackColorAsync(
        [Description("Playlist track 1-500")] int track,
        [Description("RGB hex, e.g. #FF8800")] string rgbHex, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("track", track, 1, 500) is { } e) return e;
        // Pre-validate: Convert.ToInt32 would accept junk like "FF88" (wrong color) or throw an
        // unhelpful FormatException; require exactly 6 hex digits after stripping '#'/'0x'.
        string hex = (rgbHex ?? string.Empty).Trim().TrimStart('#');
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            return Err("color must be RGB hex like #FF8800");
        int rgb = Convert.ToInt32(hex, 16);
        await _capture.ScalarAsync(InverseOps.TrackColor, JournalDict.Of("track", track), JournalDict.Of("value", rgb),
            c => fl.SetTrackColorAsync(track, rgb, c), ct);
        return Ok($"track {track} color #{rgb:X6}");
    });

    [KernelFunction("native_set_track_mute")]
    [Description("Mute/unmute a playlist track.")]
    public Task<string> SetTrackMuteAsync(
        [Description("Playlist track 1-500")] int track,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("track", track, 1, 500) is { } e) return e;
        await _capture.ScalarAsync(InverseOps.TrackMute, JournalDict.Of("track", track), JournalDict.Of("value", muted),
            c => fl.SetTrackMuteAsync(track, muted, c), ct);
        return Ok($"track {track} {(muted ? "muted" : "unmuted")}");
    });

    [KernelFunction("native_solo_track")]
    [Description("Toggle exclusive SOLO on a PLAYLIST track (call again to un-solo).")]
    public Task<string> SoloTrackAsync([Description("Playlist track 1-500")] int track, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("track", track, 1, 500) is { } e) return e;
        await fl.SetTrackSoloAsync(track, ct);
        return Ok($"toggled solo on track {track}");
    });

    [KernelFunction("native_set_track_collapsed")]
    [Description("Collapse/expand a playlist track's height.")]
    public Task<string> SetTrackCollapsedAsync(
        [Description("Playlist track 1-500")] int track,
        [Description("true = collapse, false = expand")] bool collapsed, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("track", track, 1, 500) is { } e) return e;
        await _capture.ScalarAsync(InverseOps.TrackCollapsed, JournalDict.Of("track", track), JournalDict.Of("value", collapsed),
            c => fl.SetTrackCollapsedAsync(track, collapsed, c), ct);
        return Ok($"track {track} {(collapsed ? "collapsed" : "expanded")}");
    });

    [KernelFunction("native_select_track")]
    [Description("Exclusively select a playlist track.")]
    public Task<string> SelectTrackAsync(
        [Description("Playlist track 1-500")] int track, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("track", track, 1, 500) is { } e) return e;
        await fl.SelectTrackAsync(track, ct);
        return Ok($"track {track} selected");
    });

    // ---------------- Playlist clips (arrangement) ----------------

    [KernelFunction("native_list_clips")]
    [Description("List playlist clips (slot index, track, start, length, source pattern/channel with its name), paged.")]
    public Task<string> ListClipsAsync(
        [Description("Skip first N clips; the result's continuation hint gives the next offset")] int offset = 0,
        [Description("Playlist track filter 1-500; 0 or -1 = all")] int track = -1,
        CancellationToken ct = default) => Run(async () =>
    {
        int t = track <= 0 ? -1 : track;
        if (t > 0 && Guard("track", t, 1, 500) is { } e) return e;
        return Ok(await fl.ListClipsAsync(Math.Max(offset, 0), t, ct));
    });

    // Re-enabled 2026-07-02 after the REAL crash fix: the AV was an out-of-range pattern index (FL's array
    // is ~1000 slots; ValidatePattern now caps at 999) plus the pattern's PARAM recorder (+0x20) not being
    // realized. AddPatternClipAsync now caps the index, realizes BOTH recorders, inserts atomically, and
    // refreshes. See re/generated/clipcrash-rootcause.md. (Pending harness cliptest on a CLEAN project.)
    /// <summary>Wraps a clip-mutation result with the CURRENT clip listing, so after placing/deleting clips
    /// (which change the slot-index set) the model already has the new indices for follow-up move/resize/delete
    /// and never needs a separate native_list_clips (the single biggest read-back in the usage logs). Best-effort:
    /// if the listing fails, the bare result stands.</summary>
    private async Task<string> OkWithClipsAsync(string message, CancellationToken ct)
    {
        try { return Ok($"{message}\nclips now: {await fl.ListClipsAsync(0, -1, ct)}"); }
        catch { return Ok(message); }
    }

    [KernelFunction("native_add_pattern_clips")]
    [Description("Place ONE or MANY pattern clips in ONE call. clips = JSON array of {pattern,track,start,length}, e.g. '[{\"pattern\":1,\"track\":1,\"start\":0,\"length\":0}]'. pattern 1-999 (create + add notes first); track = playlist track 1-500 (NOT a channel); ticks PPQ; length 0 = pattern length. Returns the clip list.")]
    public Task<string> AddPatternClipsAsync(
        [Description("JSON array of {pattern,track,start,length}")] string clips,
        CancellationToken ct = default) => Run(async () =>
    {
        var (specs, err) = ParseClipSpecs(clips);
        if (err is not null) return Err(err);
        if (specs.Count == 0) return Err("no clips parsed — provide '[{\"pattern\":1,\"track\":1,\"start\":0,\"length\":0}]'");
        foreach (var s in specs)
            if (Guard("track", s.Track, 1, 500) is { } te) return te;

        // Soft dedup guard (defense in depth): templates often ship with pattern clips already placed, and a
        // model that skips reading the playlist re-creates them → stacked duplicates. Read the current clips
        // ONCE and skip any spec that duplicates an ALREADY-placed clip OR an earlier spec in THIS batch —
        // only EXACT (pattern,track,start) matches are skipped; a different track or start is a legitimately
        // new placement. Lenient: if the listing fails we don't block real inserts.
        string existing;
        try { existing = await fl.ListClipsAsync(0, -1, ct); }
        catch { existing = string.Empty; }
        var toPlace = new List<PatternClipSpec>();
        var seen = new HashSet<(int, int, int)>();
        int skipped = 0;
        foreach (var s in specs)
        {
            if (ClipAlreadyPlaced(existing, s.Pattern, s.Track, s.StartTick) || !seen.Add((s.Pattern, s.Track, s.StartTick)))
            { skipped++; continue; }
            toPlace.Add(s);
        }
        if (toPlace.Count == 0) return await OkWithClipsAsync($"all {specs.Count} clip(s) already placed (skipped {skipped} duplicate(s))", ct);
        // Journal each placed clip as a Create (identity = pattern+track+start) so undo deletes it by identity.
        await _capture.PatternClipsAddedAsync(toPlace, c => fl.AddPatternClipsAsync(toPlace, c), ct);
        return await OkWithClipsAsync($"placed {toPlace.Count} clip(s)" + (skipped > 0 ? $"; skipped {skipped} duplicate(s)" : ""), ct);
    });

    /// <summary>Detects whether a pattern clip identical to (pattern, track, startTick) is already present in
    /// native_list_clips output ("[i] track T start=S len=L pattern N" — channel/audio clips parse to
    /// Pattern=-1 and never match). Only an EXACT (pattern, track, start) match counts as a duplicate — a
    /// different track or start is a new placement. Lenient by design (via <see cref="ClipList.Parse"/>):
    /// header/continuation lines and any unparsable line simply don't match, so a format hiccup never blocks
    /// a legitimate insert.</summary>
    private static bool ClipAlreadyPlaced(string clipList, int pattern, int track, int startTick) =>
        ClipList.FindSlot(ClipList.Parse(clipList), pattern, startTick, track) >= 0;

    // Bulk-argument parsing (single OR multiple items in one string) lives in BulkArgs — see its
    // class doc for the leniency contract; the `using static` keeps these call sites unchanged.

    /// <summary>Guard-style clip-index-list parse (null = ok): the shared ERR wording for a bad or
    /// empty indices arg, used by the tools that take clip slots as CSV or a JSON array.</summary>
    private static string? IndexListError(string indices, out List<int> list) =>
        TryParseIndexList(indices, out list, out var bad)
            ? null
            : Err(bad.Length > 0
                ? $"bad index '{bad}' in indices — use integers as CSV or a JSON array, e.g. '0,2,5'"
                : "no indices — provide clip slots like '0,2,5' or '[0,2,5]'");

    [KernelFunction("native_move_clips")]
    [Description("Move ONE or MANY playlist clips in ONE call. moves = JSON array of {index,start,track}, e.g. '[{\"index\":0,\"start\":0,\"track\":3}]'. index = clip slot from native_list_clips; start PPQ ticks; track = playlist 1-500, omit/0/-1 keeps current track.")]
    public Task<string> MoveClipsAsync(
        [Description("JSON array of {index,start,track}; track 0/-1/omitted = keep current")] string moves,
        CancellationToken ct = default) => Run(async () =>
    {
        var (parsed, err) = ParseMoves(moves);
        if (err is not null) return Err(err);
        if (parsed.Count == 0) return Err("no moves parsed — provide '[{\"index\":0,\"start\":0,\"track\":3}]'");
        foreach (var m in parsed)
            if (m.Track > 0 && Guard("track", m.Track, 1, 500) is { } e) return e;
        await _capture.ClipMovesAsync(parsed, c => fl.MoveClipsAsync(parsed, c), ct);
        return Ok($"moved {parsed.Count} clip(s)");
    });

    [KernelFunction("native_resize_clips")]
    [Description("Resize ONE or MANY playlist clips in ONE call. resizes = a JSON array of {index,length} (a single resize may be a 1-element array or a bare object), e.g. '[{\"index\":0,\"length\":7680},{\"index\":1,\"length\":3840}]'. index = clip slot from native_list_clips; length in PPQ ticks.")]
    public Task<string> ResizeClipsAsync(
        [Description("JSON array of {index,length} in ticks")] string resizes,
        CancellationToken ct = default) => Run(async () =>
    {
        var (parsed, err) = ParseResizes(resizes);
        if (err is not null) return Err(err);
        if (parsed.Count == 0) return Err("no resizes parsed — provide '[{\"index\":0,\"length\":7680}]'");
        await _capture.ClipResizesAsync(parsed, c => fl.ResizeClipsAsync(parsed, c), ct);
        return Ok($"resized {parsed.Count} clip(s)");
    });

    [KernelFunction("native_delete_clips")]
    [Description("Delete ONE or MANY playlist clips in ONE call. indices = clip slots from native_list_clips, as JSON array or CSV: '0,2,5' or '[0,2,5]' (single = '3').")]
    public Task<string> DeleteClipsAsync(
        [Description("Clip slots: CSV or JSON array, e.g. '0,2,5'; single '3'")] string indices,
        CancellationToken ct = default) => Run(async () =>
    {
        if (IndexListError(indices, out var list) is { } e) return e;
        int n = list.Distinct().Count();
        // Snapshot each clip's full spec BEFORE deleting so undo can re-add it (pattern clips only; an
        // audio/automation clip in the batch taints the turn → whole-commit .flp fallback).
        await _capture.ClipsDeletedAsync(list, c => fl.DeleteClipsAsync(list, c), ct);
        // Deleting renumbers the remaining slots — return the updated listing so the model has the new
        // indices instead of re-reading with native_list_clips.
        return await OkWithClipsAsync($"deleted {n} clip(s)", ct);
    });

    [KernelFunction("native_mute_clips")]
    [Description("Mute/unmute ONE or MANY playlist clips in ONE call. indices = clip slot indices from native_list_clips, as a JSON array OR a bare CSV: '0,2,5' or '[0,2,5]' (a single clip = '3'). muted: true = mute, false = unmute.")]
    public Task<string> MuteClipsAsync(
        [Description("Clip slots: CSV or JSON array, e.g. '0,2,5'; single '3'")] string indices,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) => Run(async () =>
    {
        if (IndexListError(indices, out var list) is { } e) return e;
        // Journal each clip's prior mute state (identity-addressed) so undo restores it exactly.
        await _capture.ClipMutesAsync(list, muted, c => fl.SetClipsMutedAsync(list, muted, c), ct);
        return Ok($"{(muted ? "muted" : "unmuted")} {list.Distinct().Count()} clip(s)");
    });

    [KernelFunction("native_slice_clip")]
    [Description("Slice a playlist clip into two at an absolute tick inside the clip.")]
    public Task<string> SliceClipAsync(
        [Description("Clip slot index")] int clipIndex,
        [Description("Absolute tick inside the clip")] int tick, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SliceClipAsync(clipIndex, tick, ct);
        return Ok($"clip {clipIndex} sliced @{tick}");
    });

    [KernelFunction("native_duplicate_clip")]
    [Description("Duplicate a playlist clip; copy goes right after it on the same track.")]
    public Task<string> DuplicateClipAsync(
        [Description("Clip slot index")] int clipIndex, CancellationToken ct = default) => Run(async () =>
    {
        // Journal the duplicate as a Create by diffing the clip list before/after (undo deletes the new copy
        // by identity). A non-pattern (audio/automation) duplicate has no re-addable identity → taints → .flp.
        await _capture.ClipDuplicatedAsync(c => fl.DuplicateClipAsync(clipIndex, c), ct);
        return Ok($"clip {clipIndex} duplicated");
    });

    // ---------------- Song / transport state ----------------

    [KernelFunction("native_get_song_state")]
    [Description("Read playhead position, song/pattern mode, play state, loop region, song length, tempo, master volume/shuffle/pitch.")]
    public Task<string> GetSongStateAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.GetSongStateAsync(ct)));

    [KernelFunction("native_set_song_mode")]
    [Description("Switch song mode (true) vs pattern mode (false).")]
    public Task<string> SetSongModeAsync(
        [Description("true = song mode, false = pattern mode")] bool song, CancellationToken ct = default) => Run(async () =>
    {
        await _capture.ScalarAsync(InverseOps.SongMode, JournalDict.Empty, JournalDict.Of("value", song),
            c => fl.SetSongModeAsync(song, c), ct);
        return Ok($"{(song ? "song" : "pattern")} mode");
    });

    [KernelFunction("native_seek")]
    [Description("Move song playhead to an absolute tick (clamps to song length).")]
    public Task<string> SeekAsync(
        [Description("Absolute tick")] int tick, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SeekAsync(tick, ct);
        return Ok($"playhead @{tick}");
    });

    [KernelFunction("native_list_markers")]
    [Description("List timeline markers (name, tick, bar).")]
    public Task<string> ListMarkersAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListMarkersAsync(ct)));

    // DISABLED (not a [KernelFunction]) 2026-07-08 — this tool FREEZES THE DAW. FLtr_AddTimelineMarkerCore
    // (0xd523c0) enters a critical section, and its dynarray insert FAULTS on the main thread (confirmed:
    // "call d523c0 … faulted (ok:0)" precedes every "DAW suspended processing" freeze). The bridge's SEH
    // guard catches the AV so FL doesn't crash, but LeaveCriticalSection is skipped → the main thread leaks
    // the lock the AUDIO thread needs → playback suspended, playhead frozen. FL's own EEMarkerAddMenuClick
    // uses the SAME args but wraps the core in an undo/edit transaction (FUN_00f37ec0) first; calling the
    // core raw is the suspected fault. Re-enable only after (a) the root fault is pinned + fixed AND (b) the
    // bridge gains a critical-section-safe call path that releases the lock on fault. See [[fl-control-catalog]].
    // [KernelFunction("native_add_marker")]
    [Description("Add a timeline marker at a tick (e.g. 'Verse', 'Drop').")]
    public Task<string> AddMarkerAsync(
        [Description("Tick")] int tick,
        [Description("Marker name")] string name, CancellationToken ct = default) => Run(async () =>
    {
        await fl.AddMarkerAsync(tick, name, ct);
        return Ok($"marker '{name}' @{tick}");
    });

    // ---------------- Project lifecycle ----------------

    [KernelFunction("native_save_project")]
    [Description("Save project: empty path = current file (fails if never saved), or absolute .flp path. Confirm first.")]
    public Task<string> SaveProjectAsync(
        [Description("Absolute .flp path, or empty = current file")] string path = "", CancellationToken ct = default) => Run(async () =>
    {
        await fl.SaveProjectAsync(path, ct);
        return Ok(string.IsNullOrWhiteSpace(path) ? "saved" : $"saved -> {path}");
    });

    [KernelFunction("native_open_project")]
    [Description("Open an .flp by absolute path, REPLACING current project (unsaved changes lost). Confirm first.")]
    public Task<string> OpenProjectAsync(
        [Description("Absolute .flp path")] string path, CancellationToken ct = default) => Run(async () =>
    {
        await fl.OpenProjectAsync(path, ct);
        return Ok($"opened {path}");
    });

    [KernelFunction("native_new_project")]
    [Description("Start a new empty project, REPLACING current (unsaved changes lost). Confirm first.")]
    public Task<string> NewProjectAsync(CancellationToken ct = default) => Run(async () =>
    {
        await fl.NewProjectAsync(ct);
        return Ok("new project");
    });

    [KernelFunction("native_get_project_info")]
    [Description("Report project title, file path, saved-or-untitled.")]
    public Task<string> GetProjectInfoAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.GetProjectInfoAsync(ct)));

    [KernelFunction("native_save_project_as")]
    [Description("Save As: save to a new absolute .flp path AND make it current. Confirm first.")]
    public Task<string> SaveProjectAsAsync(
        [Description("Absolute .flp path")] string path, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SaveProjectAsAsync(path, ct);
        return Ok($"saved as {path} (now current)");
    });

    [KernelFunction("native_save_copy")]
    [Description("Save a COPY to an absolute .flp path without changing the current project (safe backup/export).")]
    public Task<string> SaveCopyAsync(
        [Description("Absolute .flp path for the copy")] string path, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SaveCopyAsync(path, ct);
        return Ok($"copy -> {path}");
    });

    [KernelFunction("native_save_new_version")]
    [Description("Save an auto-incremented new version (song_2.flp, …) and make it current.")]
    public Task<string> SaveNewVersionAsync(CancellationToken ct = default) => Run(async () =>
    {
        await fl.SaveNewVersionAsync(ct);
        return Ok("new version saved");
    });

    [KernelFunction("native_list_recent_projects")]
    [Description("List recently-opened projects, most recent first.")]
    public Task<string> ListRecentProjectsAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListRecentProjectsAsync(ct)));

    // ---------------- Arrangements ----------------

    [KernelFunction("native_list_arrangements")]
    [Description("List arrangements with indices (current marked *).")]
    public Task<string> ListArrangementsAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListArrangementsAsync(ct)));

    [KernelFunction("native_make_arrangement")]
    [Description("Create a new empty arrangement + switch to it; returns new index.")]
    public Task<string> MakeArrangementAsync(
        [Description("Optional name; empty = FL default")] string name = "", CancellationToken ct = default) => Run(async () =>
    {
        string? nm = string.IsNullOrWhiteSpace(name) ? null : name;
        // Journal as a Create keyed by the returned index; undo deletes that arrangement, redo re-makes it.
        int i = await _capture.ArrangementCreatedAsync(c => fl.AddArrangementAsync(nm, c), clone: false, src: 0, name: nm, ct);
        return Ok($"arrangement {i} created{(nm is null ? string.Empty : $" '{name}'")}");
    });

    [KernelFunction("native_clone_arrangement")]
    [Description("Clone an arrangement (deep copy of tracks + clips) into a new one + switch to it.")]
    public Task<string> CloneArrangementAsync(
        [Description("Source index; 0 or -1 = current")] int srcIndex = -1,
        [Description("Optional name for the clone")] string name = "", CancellationToken ct = default) => Run(async () =>
    {
        // 0 or negative = current: weak models use 0 and -1 interchangeably for "the current one".
        // To clone arrangement 0 explicitly while another is current, select it first, then clone.
        string? nm = string.IsNullOrWhiteSpace(name) ? null : name;
        // Resolve a CONCRETE source index NOW so a redo re-clones from the same source (passing "-1 = current"
        // would re-resolve against whatever the undo made current). Undo deletes the returned clone index.
        int resolvedSrc = srcIndex > 0 ? srcIndex : ArrangementList.CurrentIndex(await fl.ListArrangementsAsync(ct));
        int i = await _capture.ArrangementCreatedAsync(
            c => fl.CloneArrangementAsync(srcIndex <= 0 ? -1 : srcIndex, nm, c), clone: true, src: resolvedSrc, name: nm, ct);
        return Ok($"cloned -> arrangement {i}{(nm is null ? string.Empty : $" '{name}'")}");
    });

    [KernelFunction("native_rename_arrangement")]
    [Description("Rename an arrangement by index (persistence across save/reload not guaranteed yet).")]
    public Task<string> RenameArrangementAsync(
        [Description("Arrangement index")] int index,
        [Description("New name")] string name, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("arrangement index", index, 0, int.MaxValue) is { } e) return e;
        await _capture.ScalarAsync(InverseOps.ArrangementName, JournalDict.Of("arrangement", index), JournalDict.Of("value", name),
            c => fl.RenameArrangementAsync(index, name, c), ct);
        return Ok($"arrangement {index} = '{name}'");
    });

    [KernelFunction("native_delete_arrangement")]
    [Description("Delete an arrangement by index.")]
    public Task<string> DeleteArrangementAsync(
        [Description("Arrangement index")] int index, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("arrangement index", index, 0, int.MaxValue) is { } e) return e;
        await fl.DeleteArrangementAsync(index, ct);
        return Ok($"arrangement {index} deleted");
    });

    [KernelFunction("native_select_arrangement")]
    [Description("Switch to the arrangement at index.")]
    public Task<string> SelectArrangementAsync(
        [Description("Arrangement index")] int index, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("arrangement index", index, 0, int.MaxValue) is { } e) return e;
        await fl.SelectArrangementAsync(index, ct);
        return Ok($"arrangement {index} selected");
    });

    // ---------------- Automation clips ----------------

    [KernelFunction("native_list_automation_points")]
    [Description("List an automation-clip channel's points (time in beats, value, tension). Channel must host the Automation Clip generator.")]
    public Task<string> ListAutomationPointsAsync(
        [Description("Automation-clip channel index")] int channel, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        return Ok(await fl.ListAutomationPointsAsync(channel, ct));
    });

    [KernelFunction("native_add_automation_point")]
    [Description("Add automation point: time in beats (4 = 1 bar), value 0.0-1.0, tension -1..1 (0 = linear).")]
    public Task<string> AddAutomationPointAsync(
        [Description("Automation-clip channel index")] int channel,
        [Description("Time in beats")] double timeBeats,
        [Description("Value 0.0-1.0")] double value,
        [Description("Tension -1..1 (0 = linear)")] double tension = 0, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        double v = Math.Clamp(value, 0.0, 1.0);
        double tn = Math.Clamp(tension, -1.0, 1.0);
        // Journal as a Create (identity = channel+time); undo deletes the point, redo re-adds the same spec.
        await _capture.AutomationAddedAsync(channel, timeBeats, v, tn,
            c => fl.AddAutomationPointAsync(channel, timeBeats, v, tn, c), ct);
        return ClampReport($"auto point chan {channel} @beat {timeBeats:0.###}", value, v, 0.0, 1.0);
    });

    [KernelFunction("native_delete_automation_point")]
    [Description("Delete an automation point by index from an automation-clip channel.")]
    public Task<string> DeleteAutomationPointAsync(
        [Description("Automation-clip channel index")] int channel,
        [Description("Point index")] int index, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } ce) return ce;
        if (Guard("point index", index, 0, int.MaxValue) is { } pe) return pe;
        // Snapshot the point BEFORE deleting so undo re-adds it (a non-zero curve, which AddAutomationPoint
        // can't reproduce, taints the turn → .flp rather than a lossy undo).
        await _capture.AutomationDeletedAsync(channel, index, c => fl.DeleteAutomationPointAsync(channel, index, c), ct);
        return Ok($"auto point {index} deleted (chan {channel})");
    });

    // native_render is intentionally NOT a [KernelFunction]: FLproj_FileExportFormat opens a MODAL
    // export dialog that runs a nested message loop on FL's main thread, which STOPS playback and
    // blocks the main thread until the user dismisses it. Letting the agent trigger that unprompted
    // mid-task is disruptive (and was a latent freeze class before OpenExportDialogAsync was made
    // non-blocking). Export stays a user-initiated action; this method is retained for an explicit
    // user "export" intent invoked from the UI, and OpenExportDialogAsync is now non-blocking so even
    // that path can never wedge the bridge. Re-expose only behind an explicit user-intent gate.
    public Task<string> RenderAsync(CancellationToken ct = default) => Run(async () =>
    {
        await fl.OpenExportDialogAsync(0, ct);
        return Ok("export dialog open — choose format/path and click Render");
    });
}
