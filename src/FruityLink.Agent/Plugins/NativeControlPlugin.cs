using System.ComponentModel;
using System.Text.Json;
using FruityLink.Agent.Versioning;
using FruityLink.Core.Abstractions;
using Microsoft.SemanticKernel;
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

    [KernelFunction("native_route_channel_to_mixer")]
    [Description("Route a channel to a mixer track 0-125.")]
    public Task<string> SetChannelFxRouteAsync(
        [Description("Channel index")] int channel,
        [Description("Mixer track 0-125")] int mixerTrack, CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } ce) return ce;
        if (Guard("mixer track", mixerTrack, 0, 125) is { } me) return me;
        await _capture.ScalarAsync(InverseOps.ChannelRoute, JournalDict.Of("channel", channel), JournalDict.Of("value", mixerTrack),
            c => fl.SetChannelFxRouteAsync(channel, mixerTrack, c), ct);
        return Ok($"chan {channel} -> mixer {mixerTrack}");
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
        [Description("Default channel for notes without a 5th field")] int channel,
        [Description("Entries 'key,start,length[,velocity[,channel]]' sep by ';' or newline")] string notes,
        CancellationToken ct = default) => Run(async () =>
    {
        if (Guard("channel", channel, 0, int.MaxValue) is { } e) return e;
        var (parsed, skipped) = ParseNotes(notes, channel);
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

    /// <summary>Parse the native_add_notes string ('key,start,length[,velocity[,channel]]' per note,
    /// separated by ';' or newlines) into NoteSpecs. Tolerant by design for weak backends: accepts
    /// decimals (rounded), treats velocity as optional (defaults to 100), and SKIPS malformed lines
    /// (returned separately) instead of throwing away the whole batch — one typo shouldn't lose a
    /// 32-note melody. Notes without a channel field use <paramref name="defaultChannel"/>.</summary>
    private static (List<NoteSpec> Parsed, List<string> Skipped) ParseNotes(string notes, int defaultChannel)
    {
        var list = new List<NoteSpec>();
        var skipped = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return (list, skipped);
        foreach (var item in notes.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = item.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 3 || !TryNum(f[0], out int key) || !TryNum(f[1], out int start) || !TryNum(f[2], out int len))
            {
                skipped.Add(item);
                continue;
            }
            int vel = 100;
            if (f.Length >= 4 && f[3].Length > 0 && !TryNum(f[3], out vel)) { skipped.Add(item); continue; }
            int chan = defaultChannel;
            if (f.Length >= 5 && f[4].Length > 0 && !TryNum(f[4], out chan)) { skipped.Add(item); continue; }
            list.Add(new NoteSpec(chan, key, start, len, vel));
        }
        return (list, skipped);
    }

    [KernelFunction("native_get_ppq")]
    [Description("Get timebase: ticks per quarter note (PPQ).")]
    public Task<string> GetPpqAsync(CancellationToken ct = default) => Run(async () =>
        Ok($"{await fl.GetPpqAsync(ct)} ticks per quarter note"));

    // ---------------- Patterns ----------------

    [KernelFunction("native_list_patterns")]
    [Description("List patterns with content as 'index: name', marking current.")]
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
    [Description("Create new empty pattern; returns its number.")]
    public Task<string> CreatePatternAsync(CancellationToken ct = default) => Run(async () =>
        Ok($"pattern {await fl.CreatePatternAsync(ct)} created + selected"));

    [KernelFunction("native_clear_pattern")]
    [Description("Delete all notes in a pattern (1-based).")]
    public Task<string> ClearPatternAsync([Description("Pattern 1-based")] int index, CancellationToken ct = default) => Run(async () =>
    {
        await fl.ClearPatternAsync(index, ct);
        return Ok($"pattern {index} cleared");
    });

    // ---------------- Channels ----------------

    [KernelFunction("native_list_channels")]
    [Description("List channels as 'index: name' — map instrument names to the index other tools need.")]
    public Task<string> ListChannelsAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListChannelsAsync(ct)));

    [KernelFunction("native_select_channel")]
    [Description("Select a channel (0-based) in the UI. NOT needed before note tools — they take an explicit channel.")]
    public Task<string> SelectChannelAsync([Description("Channel 0-based")] int index, CancellationToken ct = default) => Run(async () =>
    {
        await fl.SelectChannelAsync(index, ct);
        return Ok($"channel {index} selected");
    });

    // ---------------- Mixer (sends / EQ) ----------------

    [KernelFunction("native_list_mixer_tracks")]
    [Description("List NAMED mixer tracks as 'index: name' to map a bus/track NAME to the index mixer tools need " +
                 "(0=Master, 1-125=Inserts, 126=Current). Call FIRST when the user names a bus instead of a number; " +
                 "don't scan channels.")]
    public Task<string> ListMixerTracksAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListMixerTracksAsync(ct)));

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
    [Description("Report a channel's loaded generator plugin.")]
    public Task<string> GetChannelPluginAsync(
        [Description("Channel 0-based")] int channel, CancellationToken ct = default) => Run(async () =>
        Ok(await fl.GetChannelPluginAsync(channel, ct)));

    [KernelFunction("native_add_channel")]
    [Description("Add a channel hosting the named generator; returns new channel index.")]
    public Task<string> AddChannelAsync(
        [Description("Generator name from native_list_available_plugins")] string plugin, CancellationToken ct = default) => Run(async () =>
        Ok($"channel {await fl.AddChannelAsync(plugin, ct)} = '{plugin}'"));

    [KernelFunction("native_list_mixer_effects")]
    [Description("List effects in a mixer track's 10 FX slots.")]
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
        var (items, err) = ParseParams(@params);
        if (err is not null) return Err(err);
        if (items.Count == 0) return Err("no params parsed — provide '[{\"index\":205,\"value\":0.5}]'");
        foreach (var (index, value) in items)
        {
            if (Guard("param index", index, 0, int.MaxValue) is { } pe) return pe;
            await fl.SetPluginParamAsync(channel, -1, index, Math.Clamp(value, 0.0, 1.0), ct);
        }
        return Ok($"set {items.Count} param(s) on chan {channel}");
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
        var (items, err) = ParseParams(@params);
        if (err is not null) return Err(err);
        if (items.Count == 0) return Err("no params parsed — provide '[{\"index\":3,\"value\":0.5}]'");
        foreach (var (index, value) in items)
        {
            if (Guard("param index", index, 0, int.MaxValue) is { } pe) return pe;
            await fl.SetPluginParamAsync(track, slot, index, Math.Clamp(value, 0.0, 1.0), ct);
        }
        return Ok($"set {items.Count} param(s) on mixer {track} slot {slot}");
    });

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

    // ---------------- Notes (read) ----------------

    [KernelFunction("native_get_notes")]
    [Description("Read a pattern's piano-roll notes (channel, pitch, position, length, velocity), paged. Call ONLY when you need existing notes — not before adding new ones.")]
    public Task<string> GetNotesAsync(
        [Description("Pattern 1-based; 0 or -1 = current")] int pattern = 0,
        [Description("Channel filter; -1 = all")] int channel = -1,
        [Description("Skip first N notes; the result's continuation hint gives the next offset")] int offset = 0,
        CancellationToken ct = default) => Run(async () =>
        Ok(await fl.GetNotesAsync(pattern, channel, offset, ct)));

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
    [Description("List playlist clips (slot index, track, start, length, source pattern/channel), paged.")]
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
    [KernelFunction("native_add_pattern_clips")]
    [Description("Place ONE or MANY pattern clips in ONE call. clips = JSON array of {pattern,track,start,length}, e.g. '[{\"pattern\":1,\"track\":1,\"start\":0,\"length\":0}]'. pattern 1-999 (create + add notes first); track = playlist track 1-500 (NOT a channel); ticks PPQ; length 0 = pattern length.")]
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
        if (toPlace.Count == 0) return Ok($"all {specs.Count} clip(s) already placed (skipped {skipped} duplicate(s))");
        // Journal each placed clip as a Create (identity = pattern+track+start) so undo deletes it by identity.
        await _capture.PatternClipsAddedAsync(toPlace, c => fl.AddPatternClipsAsync(toPlace, c), ct);
        return Ok($"placed {toPlace.Count} clip(s)" + (skipped > 0 ? $"; skipped {skipped} duplicate(s)" : ""));
    });

    /// <summary>Detects whether a pattern clip identical to (pattern, track, startTick) is already present in
    /// native_list_clips output. Clip lines look like "[i] track T start=S len=L pattern N" (the source token
    /// is "pattern N" for pattern clips, "channel N" for audio/automation clips). Only an EXACT
    /// (pattern, track, start) match counts as a duplicate — a different track or start is a new placement.
    /// Lenient by design: header/continuation lines and any unparsable line simply don't match, so a format
    /// hiccup never blocks a legitimate insert.</summary>
    private static bool ClipAlreadyPlaced(string clipList, int pattern, int track, int startTick)
    {
        if (string.IsNullOrWhiteSpace(clipList)) return false;
        foreach (var line in clipList.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A pattern clip line carries all three tokens; channel clips lack "pattern " (p stays null → no match).
            if (FieldAfter(line, "pattern ") == pattern
                && FieldAfter(line, "track ") == track
                && FieldAfter(line, "start=") == startTick)
                return true;
        }
        return false;
    }

    /// <summary>Read the signed integer that immediately follows <paramref name="token"/> in a clip line
    /// (e.g. "start=" -> 384, "pattern " -> 1), or null when the token is absent or not followed by a number.</summary>
    private static int? FieldAfter(string line, string token)
    {
        int i = line.IndexOf(token, StringComparison.Ordinal);
        if (i < 0) return null;
        int j = i + token.Length, k = j;
        if (k < line.Length && (line[k] == '-' || line[k] == '+')) k++;
        while (k < line.Length && char.IsDigit(line[k])) k++;
        return int.TryParse(line.AsSpan(j, k - j), out int v) ? v : null;
    }

    // ---------------- Bulk-argument parsing (single OR multiple in one string) ----------------
    // The batchable clip/param tools take a SINGLE string arg that is parsed leniently, because weak
    // backends vary wildly in how they emit a list. Index lists accept CSV or a JSON array; object
    // lists (moves/resizes/adds/params) accept a JSON array, a bare single object, and case-insensitive
    // field names with a few aliases. On a hard parse failure the tool returns a clear ERR naming the
    // offending item so the model can self-correct instead of retrying blind.

    private static readonly JsonDocumentOptions LenientJson =
        new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Parse an index list: a JSON array ("[0,2,5]"), a bare CSV ("0,2,5"), or a single value
    /// ("3"). Tolerates surrounding brackets, and comma/space/semicolon separators. Returns false with
    /// <paramref name="bad"/> = the first non-numeric token (empty when simply nothing was provided).</summary>
    private static bool TryParseIndexList(string raw, out List<int> result, out string bad)
    {
        result = new List<int>();
        bad = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim().Trim('[', ']', '(', ')');
        foreach (var tok in s.Split(new[] { ',', ' ', '\t', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNum(tok, out int v)) result.Add(v);
            else { bad = tok; result.Clear(); return false; }
        }
        return result.Count > 0;
    }

    /// <summary>Normalize a JSON object-or-array string into a list of object elements. Returns the parse
    /// error (or null on success). The caller must keep <paramref name="doc"/> alive while reading elements.</summary>
    private static string? ParseObjectList(string raw, out JsonDocument? doc, out List<JsonElement> objs)
    {
        doc = null;
        objs = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(raw)) return "empty input — provide a JSON array of objects";
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(raw, LenientJson); }
        catch (JsonException ex) { return $"not valid JSON ({ex.Message})"; }
        var root = parsed.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) { parsed.Dispose(); return "each list entry must be a JSON object like {\"index\":0,...}"; }
                objs.Add(e);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            objs.Add(root);   // tolerate a single bare object as a 1-element list
        }
        else { parsed.Dispose(); return "expected a JSON array of objects (or a single object)"; }
        doc = parsed;
        return null;
    }

    /// <summary>Read a numeric field by any of <paramref name="names"/> (case-insensitive), accepting a JSON
    /// number or a numeric string. Returns false if absent or non-numeric.</summary>
    private static bool TryGetNum(JsonElement o, out double value, params string[] names)
    {
        value = 0;
        foreach (var prop in o.EnumerateObject())
        {
            if (!names.Any(n => string.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
            var v = prop.Value;
            if (v.ValueKind == JsonValueKind.Number) { value = v.GetDouble(); return true; }
            if (v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value))
                return true;
            return false;   // present but wrong type
        }
        return false;
    }

    private static bool TryGetInt(JsonElement o, out int value, params string[] names)
    {
        value = 0;
        if (!TryGetNum(o, out double d, names)) return false;
        value = (int)Math.Round(d);
        return true;
    }

    /// <summary>Parse the native_move_clips arg into ClipMoves. Requires index + start; track is optional
    /// (missing or &lt;= 0 = keep current, encoded as -1).</summary>
    private static (List<ClipMove> Parsed, string? Err) ParseMoves(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<ClipMove>(), $"moves: {err}");
            var list = new List<ClipMove>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int index, "index", "idx", "clip", "clipIndex", "slot"))
                    return (list, $"move #{i + 1} is missing a numeric 'index'");
                if (!TryGetInt(objs[i], out int start, "start", "startTick", "tick", "pos", "position"))
                    return (list, $"move #{i + 1} is missing a numeric 'start'");
                int track = TryGetInt(objs[i], out int t, "track", "trk") && t > 0 ? t : -1;
                list.Add(new ClipMove(index, start, track));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the native_resize_clips arg into ClipResizes (index + length required).</summary>
    private static (List<ClipResize> Parsed, string? Err) ParseResizes(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<ClipResize>(), $"resizes: {err}");
            var list = new List<ClipResize>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int index, "index", "idx", "clip", "clipIndex", "slot"))
                    return (list, $"resize #{i + 1} is missing a numeric 'index'");
                if (!TryGetInt(objs[i], out int length, "length", "len", "lengthTick"))
                    return (list, $"resize #{i + 1} is missing a numeric 'length'");
                list.Add(new ClipResize(index, length));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the native_add_pattern_clips arg into PatternClipSpecs (pattern + track + start
    /// required; length optional, defaults 0 = the pattern's own length).</summary>
    private static (List<PatternClipSpec> Parsed, string? Err) ParseClipSpecs(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<PatternClipSpec>(), $"clips: {err}");
            var list = new List<PatternClipSpec>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int pattern, "pattern", "pat", "patternIndex"))
                    return (list, $"clip #{i + 1} is missing a numeric 'pattern'");
                if (!TryGetInt(objs[i], out int track, "track", "trk"))
                    return (list, $"clip #{i + 1} is missing a numeric 'track'");
                if (!TryGetInt(objs[i], out int start, "start", "startTick", "tick", "pos", "position"))
                    return (list, $"clip #{i + 1} is missing a numeric 'start'");
                int length = TryGetInt(objs[i], out int len, "length", "len", "lengthTick") ? len : 0;
                list.Add(new PatternClipSpec(pattern, track, start, length));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the plugin-param arg into (index, value) pairs (both required per entry).</summary>
    private static (List<(int Index, double Value)> Parsed, string? Err) ParseParams(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<(int, double)>(), $"params: {err}");
            var list = new List<(int, double)>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int index, "index", "idx", "param", "paramIndex", "i"))
                    return (list, $"param #{i + 1} is missing a numeric 'index'");
                if (!TryGetNum(objs[i], out double value, "value", "val", "v"))
                    return (list, $"param #{i + 1} is missing a numeric 'value'");
                list.Add((index, value));
            }
            return (list, null);
        }
    }

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
        if (!TryParseIndexList(indices, out var list, out var bad))
            return Err(bad.Length > 0
                ? $"bad index '{bad}' in indices — use integers as CSV or a JSON array, e.g. '0,2,5'"
                : "no indices — provide clip slots like '0,2,5' or '[0,2,5]'");
        int n = list.Distinct().Count();
        // Snapshot each clip's full spec BEFORE deleting so undo can re-add it (pattern clips only; an
        // audio/automation clip in the batch taints the turn → whole-commit .flp fallback).
        await _capture.ClipsDeletedAsync(list, c => fl.DeleteClipsAsync(list, c), ct);
        return Ok($"deleted {n} clip(s)");
    });

    [KernelFunction("native_mute_clips")]
    [Description("Mute/unmute ONE or MANY playlist clips in ONE call. indices = clip slot indices from native_list_clips, as a JSON array OR a bare CSV: '0,2,5' or '[0,2,5]' (a single clip = '3'). muted: true = mute, false = unmute.")]
    public Task<string> MuteClipsAsync(
        [Description("Clip slots: CSV or JSON array, e.g. '0,2,5'; single '3'")] string indices,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) => Run(async () =>
    {
        if (!TryParseIndexList(indices, out var list, out var bad))
            return Err(bad.Length > 0
                ? $"bad index '{bad}' in indices — use integers as CSV or a JSON array, e.g. '0,2,5'"
                : "no indices — provide clip slots like '0,2,5' or '[0,2,5]'");
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
    [Description("Read playhead position, song/pattern mode, play state, loop region, song length.")]
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
    [Description("List timeline markers (name + tick).")]
    public Task<string> ListMarkersAsync(CancellationToken ct = default) => Run(async () =>
        Ok(await fl.ListMarkersAsync(ct)));

    [KernelFunction("native_add_marker")]
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
