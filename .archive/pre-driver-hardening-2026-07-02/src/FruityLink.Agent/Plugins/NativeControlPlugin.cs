using System.ComponentModel;
using FruityLink.Core.Abstractions;
using Microsoft.SemanticKernel;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Direct native control of FL Studio through the injected DLL bridge (command-bus param protocol).
/// These tools execute natively inside FL and cover the master/mixer/channel parameter surface —
/// more powerful than, and complementary to, the MIDI-script tools. Values use FL's native integer
/// scales (see each parameter description).
/// </summary>
public sealed class NativeControlPlugin(INativeFlControl fl)
{
    [KernelFunction("native_is_available")]
    [Description("Diagnostics only: check if the native bridge is loaded + responding. Don't call as a warm-up — every other native_* tool already returns a clear bridge error if it's down. Use only to investigate a failure.")]
    public async Task<string> IsAvailableAsync(CancellationToken ct = default)
    {
        try { return await fl.IsAvailableAsync(ct) ? "Native bridge is available." : "Native bridge is NOT injected."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_tempo")]
    [Description("Set tempo, BPM (10-522).")]
    public async Task<string> SetTempoAsync([Description("BPM")] double bpm, CancellationToken ct = default)
    {
        try { await fl.SetTempoAsync(bpm, ct); return $"Tempo set to {bpm:0.##} BPM."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_get_tempo")]
    [Description("Read tempo (BPM).")]
    public async Task<string> GetTempoAsync(CancellationToken ct = default)
    {
        try { return $"{await fl.GetTempoAsync(ct):0.##} BPM"; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_master_volume")]
    [Description("Set master volume, 0-12800 (~7624 = unity/0 dB).")]
    public async Task<string> SetMasterVolumeAsync([Description("0-12800")] int value, CancellationToken ct = default)
    {
        try
        {
            int v = Math.Clamp(value, 0, 12800);
            await fl.SetMasterVolumeAsync(v, ct);
            return v == value ? $"Master volume set to {v}." : $"Master volume set to {v} (requested {value}, clamped to 0-12800).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_master_pitch")]
    [Description("Set master pitch, cents -1200..1200.")]
    public async Task<string> SetMasterPitchAsync([Description("cents -1200..1200")] int cents, CancellationToken ct = default)
    {
        try
        {
            int c = Math.Clamp(cents, -1200, 1200);
            await fl.SetMasterPitchAsync(c, ct);
            return c == cents ? $"Master pitch set to {c} cents." : $"Master pitch set to {c} cents (requested {cents}, clamped to -1200..1200).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_shuffle")]
    [Description("Set global shuffle/swing, 0-128.")]
    public async Task<string> SetShuffleAsync([Description("0-128")] int value, CancellationToken ct = default)
    {
        try
        {
            int v = Math.Clamp(value, 0, 128);
            await fl.SetShuffleAsync(v, ct);
            return v == value ? $"Shuffle set to {v}." : $"Shuffle set to {v} (requested {value}, clamped to 0-128).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_mixer_volume")]
    [Description("Set mixer track volume, 0-12800 (track 0 = master).")]
    public async Task<string> SetMixerVolumeAsync(
        [Description("Mixer track (0 = master)")] int track,
        [Description("0-12800")] int value, CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Mixer track", track, 0, 125) is { } e) return e;
            int v = Math.Clamp(value, 0, 12800);
            await fl.SetMixerVolumeAsync(track, v, ct);
            return v == value ? $"Mixer track {track} volume = {v}." : $"Mixer track {track} volume = {v} (requested {value}, clamped to 0-12800).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_mixer_pan")]
    [Description("Set mixer track pan, 0-12800 (6400 = center).")]
    public async Task<string> SetMixerPanAsync(
        [Description("Mixer track")] int track,
        [Description("0-12800, 6400=center")] int value, CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Mixer track", track, 0, 125) is { } e) return e;
            int v = Math.Clamp(value, 0, 12800);
            await fl.SetMixerPanAsync(track, v, ct);
            return v == value ? $"Mixer track {track} pan = {v}." : $"Mixer track {track} pan = {v} (requested {value}, clamped to 0-12800).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // NOTE: intentionally NOT a [KernelFunction] — it took a raw "normalized fixed-point" long with no
    // stated range, which the model could not use correctly and confused with native_set_mixer_plugin_param
    // (normalized 0.0-1.0). Kept as an internal method; the LLM-facing setter is native_set_mixer_plugin_param.
    public async Task<string> SetMixerFxParamAsync(
        int track, int slot, int paramIndex, long value, CancellationToken ct = default)
    {
        try { await fl.SetMixerFxParamAsync(track, slot, paramIndex, value, ct); return $"Mixer {track} slot {slot} param {paramIndex} set."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_channel_volume")]
    [Description("Set channel volume, 0-12800 (10000 = default).")]
    public async Task<string> SetChannelVolumeAsync(
        [Description("Channel index")] int channel,
        [Description("0-12800")] int value, CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Channel", channel, 0, int.MaxValue) is { } e) return e;
            int v = Math.Clamp(value, 0, 12800);
            await fl.SetChannelVolumeAsync(channel, v, ct);
            return v == value ? $"Channel {channel} volume = {v}." : $"Channel {channel} volume = {v} (requested {value}, clamped to 0-12800).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_channel_pan")]
    [Description("Set channel pan, 0-12800 (6400 = center).")]
    public async Task<string> SetChannelPanAsync(
        [Description("Channel index")] int channel,
        [Description("0-12800, 6400=center")] int value, CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Channel", channel, 0, int.MaxValue) is { } e) return e;
            int v = Math.Clamp(value, 0, 12800);
            await fl.SetChannelPanAsync(channel, v, ct);
            return v == value ? $"Channel {channel} pan = {v}." : $"Channel {channel} pan = {v} (requested {value}, clamped to 0-12800).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_channel_pitch")]
    [Description("Set channel pitch, cents (0 = center).")]
    public async Task<string> SetChannelPitchAsync(
        [Description("Channel index")] int channel,
        [Description("cents (0=center)")] int cents, CancellationToken ct = default)
    {
        try { await fl.SetChannelPitchAsync(channel, cents, ct); return $"Channel {channel} pitch = {cents} cents."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_channel_muted")]
    [Description("Mute/unmute a channel.")]
    public async Task<string> SetChannelMutedAsync(
        [Description("Channel index")] int channel,
        [Description("true=mute, false=unmute")] bool muted, CancellationToken ct = default)
    {
        try { await fl.SetChannelMutedAsync(channel, muted, ct); return $"Channel {channel} {(muted ? "muted" : "unmuted")}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_route_channel_to_mixer")]
    [Description("Route a channel to a mixer track (0-125).")]
    public async Task<string> SetChannelFxRouteAsync(
        [Description("Channel index")] int channel,
        [Description("Mixer track 0-125")] int mixerTrack, CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Channel", channel, 0, int.MaxValue) is { } ce) return ce;
            if (RangeError("Mixer track", mixerTrack, 0, 125) is { } me) return me;
            await fl.SetChannelFxRouteAsync(channel, mixerTrack, ct);
            return $"Channel {channel} routed to mixer track {mixerTrack}.";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_note")]
    [Description("Add ONE note to a pattern's piano roll. For >1 note (melodies/chords/drums/basslines) use native_add_notes — places all in one call, much faster. pattern: 1-based, or 0 = current. Positions in PPQ ticks (native_get_ppq, often 960/quarter).")]
    public async Task<string> AddNoteAsync(
        [Description("Pattern 1-based, or 0 = current")] int pattern,
        [Description("Channel index (instrument)")] int channel,
        [Description("MIDI note 0-131 (60 = middle C)")] int key,
        [Description("Start, PPQ ticks from pattern start")] int startTick,
        [Description("Length, PPQ ticks")] int lengthTick,
        [Description("Velocity 0-127 (100 = default)")] int velocity = 100,
        CancellationToken ct = default)
    {
        try { await fl.AddNoteAsync(pattern, channel, key, startTick, lengthTick, velocity, ct); return $"Added note key={key} @tick {startTick} (len {lengthTick}, vel {velocity}) on pattern {(pattern <= 0 ? "current" : pattern.ToString())}, channel {channel}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_notes")]
    [Description("Add MANY notes to a pattern's piano roll in ONE call — strongly preferred over repeated native_add_note (one bridge round + one refresh). For melodies/chords/drums/basslines. pattern: 1-based, or 0 = current. PPQ ticks (native_get_ppq, often 960/quarter). notes: list sep by ';' or newlines, each 'key,start,length,velocity' with an OPTIONAL 5th field = that note's channel — e.g. '60,0,480,100; 64,480,480,100; 67,960,480,90'. Notes without a 5th field use the channel arg. Chord = notes sharing the same start tick.")]
    public async Task<string> AddNotesAsync(
        [Description("Pattern 1-based, or 0 = current")] int pattern,
        [Description("Default channel for notes lacking their own 5th field")] int channel,
        [Description("Notes sep by ';'/newlines; each 'key,start,length,velocity[,channel]' (PPQ ticks)")] string notes,
        CancellationToken ct = default)
    {
        try
        {
            var (parsed, skipped) = ParseNotes(notes, channel);
            if (parsed.Count == 0)
                return skipped.Count > 0
                    ? $"No valid notes — {skipped.Count} malformed (e.g. '{skipped[0]}'). Use 'key,start,length[,velocity[,channel]]' with numbers."
                    : "No notes parsed — provide notes like '60,0,480,100; 64,480,480,100'.";
            await fl.AddNotesAsync(pattern, parsed, ct);
            string where = pattern <= 0 ? "current" : pattern.ToString();
            return skipped.Count == 0
                ? $"Added {parsed.Count} note(s) to pattern {where}."
                : $"Added {parsed.Count} note(s) to pattern {where}; skipped {skipped.Count} malformed (e.g. '{skipped[0]}').";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

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

    /// <summary>Lenient integer parse: accepts plain ints and decimals (rounded), since weak models
    /// often emit '480.0' where a whole number is expected.</summary>
    private static bool TryNum(string s, out int v)
    {
        if (int.TryParse(s, out v)) return true;
        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            v = (int)Math.Round(d);
            return true;
        }
        v = 0;
        return false;
    }

    /// <summary>Returns an error string when <paramref name="value"/> is outside [lo, hi], else null.
    /// Guards indices at the tool layer so a hallucinated index returns a readable message instead of
    /// building an out-of-bounds command id that could poke wrong memory / fault FL.</summary>
    private static string? RangeError(string name, int value, int lo, int hi)
        => value < lo || value > hi ? $"{name} {value} out of range ({lo}-{hi})." : null;

    [KernelFunction("native_get_ppq")]
    [Description("Get timebase: ticks per quarter note (PPQ), for note positions/lengths.")]
    public async Task<string> GetPpqAsync(CancellationToken ct = default)
    {
        try { return $"{await fl.GetPpqAsync(ct)} ticks per quarter note"; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Patterns ----------------

    [KernelFunction("native_list_patterns")]
    [Description("List patterns with content as 'index: name', marking current.")]
    public async Task<string> ListPatternsAsync(CancellationToken ct = default)
    {
        try { return await fl.ListPatternsAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_get_current_pattern")]
    [Description("Get current (selected) pattern number.")]
    public async Task<string> GetCurrentPatternAsync(CancellationToken ct = default)
    {
        try { return $"Current pattern: {await fl.GetCurrentPatternAsync(ct)}"; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_select_pattern")]
    [Description("Select pattern by number (1-based). NOT required before adding notes/clips — those take an explicit pattern (0 = current). Call only if the user asked to change the selection.")]
    public async Task<string> SelectPatternAsync([Description("Pattern 1-based")] int index, CancellationToken ct = default)
    {
        try { await fl.SelectPatternAsync(index, ct); return $"Selected pattern {index}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_create_pattern")]
    [Description("Create new empty pattern (first free slot); returns its number.")]
    public async Task<string> CreatePatternAsync(CancellationToken ct = default)
    {
        try { return $"Created and selected pattern {await fl.CreatePatternAsync(ct)}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_clear_pattern")]
    [Description("Delete all notes in a pattern (1-based).")]
    public async Task<string> ClearPatternAsync([Description("Pattern 1-based")] int index, CancellationToken ct = default)
    {
        try { await fl.ClearPatternAsync(index, ct); return $"Cleared notes in pattern {index}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Channels ----------------

    [KernelFunction("native_list_channels")]
    [Description("List channels as 'index: name' — use to map an instrument NAME to its index when you need one to act. Pass that index to native_add_note(s)/native_set_channel_*.")]
    public async Task<string> ListChannelsAsync(CancellationToken ct = default)
    {
        try { return await fl.ListChannelsAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_select_channel")]
    [Description("Select a channel (0-based) for the FL UI. NOT required before native_add_note/native_add_notes — those take an explicit channel. Call only if the user asked to change the selection.")]
    public async Task<string> SelectChannelAsync([Description("Channel 0-based")] int index, CancellationToken ct = default)
    {
        try { await fl.SelectChannelAsync(index, ct); return $"Selected channel {index}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Mixer (sends / EQ) ----------------

    [KernelFunction("native_set_mixer_send")]
    [Description("Send mixer srcTrack->dstTrack at a level (0.0-1.25, 1.0 = unity). Enables route + sets send level.")]
    public async Task<string> SetMixerSendAsync(
        [Description("Source mixer track")] int srcTrack,
        [Description("Dest mixer track")] int dstTrack,
        [Description("Send level (1.0 = unity)")] double level, CancellationToken ct = default)
    {
        try { await fl.SetMixerSendAsync(srcTrack, dstTrack, level, ct); return $"Mixer send {srcTrack}->{dstTrack} set to {level:0.###}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_mixer_eq_gain")]
    [Description("Set mixer track EQ band gain. band: 0=low,1=mid,2=high. value 0-1073741824 (~536870912 = 0 dB).")]
    public async Task<string> SetMixerEqGainAsync(
        [Description("Mixer track")] int track,
        [Description("EQ band: 0=low, 1=mid, 2=high")] int band,
        [Description("Raw gain 0..1073741824 (~536870912 = 0 dB)")] int value, CancellationToken ct = default)
    {
        try { await fl.SetMixerEqGainAsync(track, band, value, ct); return $"Mixer track {track} EQ band {band} gain set."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Transport ----------------

    [KernelFunction("native_transport_play")]
    [Description("Start playback.")]
    public async Task<string> TransportPlayAsync(CancellationToken ct = default)
    {
        try { await fl.TransportPlayAsync(ct); return "Playback started."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_transport_stop")]
    [Description("Stop playback.")]
    public async Task<string> TransportStopAsync(CancellationToken ct = default)
    {
        try { await fl.TransportStopAsync(ct); return "Playback stopped."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_transport_toggle_record")]
    [Description("Toggle record-arm.")]
    public async Task<string> TransportToggleRecordAsync(CancellationToken ct = default)
    {
        try { await fl.TransportToggleRecordAsync(ct); return "Toggled record."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Plugins / inserts ----------------

    [KernelFunction("native_list_available_plugins")]
    [Description("List installed plugins addable as inserts. kind = 'generator' (channel instruments) or 'effect' (mixer FX). Use names with native_add_channel/native_add_mixer_effect.")]
    public async Task<string> ListAvailablePluginsAsync(
        [Description("'generator' or 'effect'")] string kind, CancellationToken ct = default)
    {
        try
        {
            bool effects = kind?.Trim().ToLowerInvariant() is "effect" or "effects" or "fx";
            return await fl.ListAvailablePluginsAsync(effects, ct);
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_get_channel_plugin")]
    [Description("Report a channel's loaded generator plugin (if any).")]
    public async Task<string> GetChannelPluginAsync(
        [Description("Channel 0-based")] int channel, CancellationToken ct = default)
    {
        try { return await fl.GetChannelPluginAsync(channel, ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_channel")]
    [Description("Add a channel hosting the named generator/instrument (name from native_list_available_plugins kind='generator', e.g. 'Sytrus','FLEX','BooBass'). Returns new channel index.")]
    public async Task<string> AddChannelAsync(
        [Description("Generator name")] string plugin, CancellationToken ct = default)
    {
        try { int i = await fl.AddChannelAsync(plugin, ct); return $"Added channel {i} with '{plugin}'."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_list_mixer_effects")]
    [Description("List effects in a mixer track's 10 FX slots.")]
    public async Task<string> ListMixerEffectsAsync(
        [Description("Mixer track")] int track, CancellationToken ct = default)
    {
        try { return await fl.ListMixerEffectsAsync(track, ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_mixer_effect")]
    [Description("Load named effect into a mixer FX slot (0-9). Name from native_list_available_plugins kind='effect' (e.g. 'Fruity Reeverb 2','Fruity Limiter','Fruity Parametric EQ 2').")]
    public async Task<string> AddMixerEffectAsync(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Effect name")] string plugin, CancellationToken ct = default)
    {
        try { await fl.AddMixerEffectAsync(track, slot, plugin, ct); return $"Loaded '{plugin}' into mixer track {track}, slot {slot}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_remove_mixer_effect")]
    [Description("Clear a mixer track's FX slot.")]
    public async Task<string> RemoveMixerEffectAsync(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot, CancellationToken ct = default)
    {
        try { await fl.RemoveMixerEffectAsync(track, slot, ct); return $"Cleared mixer track {track}, slot {slot}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_clone_mixer_effect")]
    [Description("Copy effect type between FX slots on the same mixer track (type only, not param state).")]
    public async Task<string> CloneMixerEffectAsync(
        [Description("Mixer track")] int track,
        [Description("Source FX slot")] int fromSlot,
        [Description("Dest FX slot")] int toSlot, CancellationToken ct = default)
    {
        try { await fl.CloneMixerEffectAsync(track, fromSlot, toSlot, ct); return $"Copied effect from slot {fromSlot} to slot {toSlot} on track {track}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Plugin parameters (VST / native sound design) ----------------

    [KernelFunction("native_list_channel_plugin_params")]
    [Description("List a channel generator's params as 'index: name'. Optionally filter by name substring (recommended for big synths like Serum, ~1000 params). Use index with native_set_channel_plugin_param.")]
    public async Task<string> ListChannelPluginParamsAsync(
        [Description("Channel 0-based")] int channel,
        [Description("Optional name filter (e.g. 'filter','osc','cutoff'); empty = all")] string filter = "",
        CancellationToken ct = default)
    {
        try { return await fl.ListPluginParamsAsync(channel, -1, string.IsNullOrWhiteSpace(filter) ? null : filter, ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_channel_plugin_param")]
    [Description("Set a channel generator param to normalized 0.0-1.0 (e.g. Serum filter cutoff). Index from native_list_channel_plugin_params.")]
    public async Task<string> SetChannelPluginParamAsync(
        [Description("Channel index")] int channel,
        [Description("Param index")] int paramIndex,
        [Description("Normalized value 0.0-1.0")] double value, CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Channel", channel, 0, int.MaxValue) is { } ce) return ce;
            if (paramIndex < 0) return "Param index must be >= 0.";
            double v = Math.Clamp(value, 0.0, 1.0);
            await fl.SetPluginParamAsync(channel, -1, paramIndex, v, ct);
            return v == value ? $"Channel {channel} param {paramIndex} set to {v:0.###}." : $"Channel {channel} param {paramIndex} set to {v:0.###} (requested {value:0.###}, clamped to 0.0-1.0).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_list_mixer_plugin_params")]
    [Description("List a mixer FX-slot effect's params as 'index: name'. Optionally filter by name substring. Use index with native_set_mixer_plugin_param.")]
    public async Task<string> ListMixerPluginParamsAsync(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Optional name filter; empty = all")] string filter = "",
        CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Mixer track", track, 0, 125) is { } te) return te;
            if (RangeError("FX slot", slot, 0, 9) is { } se) return se;
            return await fl.ListPluginParamsAsync(track, slot, string.IsNullOrWhiteSpace(filter) ? null : filter, ct);
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_mixer_plugin_param")]
    [Description("Set a mixer FX-slot param to normalized 0.0-1.0 (e.g. a Pro-Q band freq/gain). Index from native_list_mixer_plugin_params.")]
    public async Task<string> SetMixerPluginParamAsync(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Param index")] int paramIndex,
        [Description("Normalized value 0.0-1.0")] double value, CancellationToken ct = default)
    {
        try
        {
            if (RangeError("Mixer track", track, 0, 125) is { } te) return te;
            if (RangeError("FX slot", slot, 0, 9) is { } se) return se;
            if (paramIndex < 0) return "Param index must be >= 0.";
            double v = Math.Clamp(value, 0.0, 1.0);
            await fl.SetPluginParamAsync(track, slot, paramIndex, v, ct);
            return v == value ? $"Track {track} slot {slot} param {paramIndex} set to {v:0.###}." : $"Track {track} slot {slot} param {paramIndex} set to {v:0.###} (requested {value:0.###}, clamped to 0.0-1.0).";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Samples ----------------

    [KernelFunction("native_list_samples")]
    [Description("List audio samples (FL factory packs + user content) for drums/one-shots/loops. Filter by name/path substring (e.g. 'kick','snare','808','vocal'). Use a returned full path with native_add_sample_channel.")]
    public async Task<string> ListSamplesAsync(
        [Description("Optional name/path filter; empty = broad list")] string filter = "",
        CancellationToken ct = default)
    {
        try { return await fl.ListSamplesAsync(string.IsNullOrWhiteSpace(filter) ? null : filter, ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_sample_channel")]
    [Description("Add a channel that plays the given audio sample (drum/one-shot/loop). Pass a full path (from native_list_samples). Returns new channel index.")]
    public async Task<string> AddSampleChannelAsync(
        [Description("Full path to .wav/.mp3/.flac/.ogg/.aiff sample")] string samplePath, CancellationToken ct = default)
    {
        try { int i = await fl.AddSampleChannelAsync(samplePath, ct); return $"Added sample channel {i}: {System.IO.Path.GetFileName(samplePath)}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_replace_channel_sample")]
    [Description("Replace a channel's sample with another audio file (full path).")]
    public async Task<string> ReplaceChannelSampleAsync(
        [Description("Channel index")] int channel,
        [Description("Full path to new sample")] string samplePath, CancellationToken ct = default)
    {
        try { await fl.ReplaceChannelSampleAsync(channel, samplePath, ct); return $"Replaced channel {channel} sample with {System.IO.Path.GetFileName(samplePath)}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Notes (read) ----------------

    [KernelFunction("native_get_notes")]
    [Description("Read piano-roll notes in a pattern (pattern: 1-based, or 0 = current). channel = index to filter, or -1 = all. Returns each note's channel, pitch, position (ticks), length, velocity. Call ONLY when you need the existing notes (e.g. to edit/replace/avoid them) — not as a routine step before adding new notes.")]
    public async Task<string> GetNotesAsync(
        [Description("Pattern 1-based, or 0 = current")] int pattern = 0,
        [Description("Channel to filter, or -1 = all")] int channel = -1, CancellationToken ct = default)
    {
        try { return await fl.GetNotesAsync(pattern, channel, ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Playlist tracks ----------------

    [KernelFunction("native_list_playlist_tracks")]
    [Description("List playlist tracks: name, color, mute, collapse, selection, type (normal/audio/instrument).")]
    public async Task<string> ListPlaylistTracksAsync(CancellationToken ct = default)
    {
        try { return await fl.ListPlaylistTracksAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_track_name")]
    [Description("Rename a playlist track (1-based).")]
    public async Task<string> SetTrackNameAsync(
        [Description("Playlist track 1-based")] int track,
        [Description("New name")] string name, CancellationToken ct = default)
    {
        try { await fl.SetTrackNameAsync(track, name, ct); return $"Track {track} renamed to '{name}'."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_track_color")]
    [Description("Set a playlist track color. RGB hex like '#FF8800' or 'FF8800'.")]
    public async Task<string> SetTrackColorAsync(
        [Description("Playlist track 1-based")] int track,
        [Description("RGB hex, e.g. #FF8800")] string rgbHex, CancellationToken ct = default)
    {
        try
        {
            int rgb = Convert.ToInt32(rgbHex.TrimStart('#'), 16);
            await fl.SetTrackColorAsync(track, rgb, ct);
            return $"Track {track} color set to #{rgb:X6}.";
        }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_track_mute")]
    [Description("Mute/unmute a playlist track.")]
    public async Task<string> SetTrackMuteAsync(
        [Description("Playlist track 1-based")] int track,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default)
    {
        try { await fl.SetTrackMuteAsync(track, muted, ct); return $"Track {track} {(muted ? "muted" : "unmuted")}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_track_collapsed")]
    [Description("Collapse/expand a playlist track's height.")]
    public async Task<string> SetTrackCollapsedAsync(
        [Description("Playlist track 1-based")] int track,
        [Description("true = collapse, false = expand")] bool collapsed, CancellationToken ct = default)
    {
        try { await fl.SetTrackCollapsedAsync(track, collapsed, ct); return $"Track {track} {(collapsed ? "collapsed" : "expanded")}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_select_track")]
    [Description("Exclusively select a playlist track.")]
    public async Task<string> SelectTrackAsync(
        [Description("Playlist track 1-based")] int track, CancellationToken ct = default)
    {
        try { await fl.SelectTrackAsync(track, ct); return $"Selected track {track}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Playlist clips (arrangement) ----------------

    [KernelFunction("native_list_clips")]
    [Description("List playlist clips (slot index, track, start, length, source pattern/channel).")]
    public async Task<string> ListClipsAsync(CancellationToken ct = default)
    {
        try { return await fl.ListClipsAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_pattern_clip")]
    [Description("Place a pattern clip on the playlist to arrange a song. pattern 1-based; track = playlist track; startTick/lengthTick PPQ ticks (lengthTick 0 = pattern's own length). Use native_get_ppq.")]
    public async Task<string> AddPatternClipAsync(
        [Description("Pattern 1-based")] int pattern,
        [Description("Playlist track")] int track,
        [Description("Start, ticks")] int startTick,
        [Description("Length ticks (0 = pattern length)")] int lengthTick = 0, CancellationToken ct = default)
    {
        try { await fl.AddPatternClipAsync(pattern, track, startTick, lengthTick, ct); return $"Placed pattern {pattern} on track {track} at tick {startTick}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_move_clip")]
    [Description("Move a playlist clip (slot index from native_list_clips) to a new start tick + optionally new track (-1 = keep).")]
    public async Task<string> MoveClipAsync(
        [Description("Clip slot index")] int clipIndex,
        [Description("New start, ticks")] int startTick,
        [Description("New track, or -1 = keep")] int track = -1, CancellationToken ct = default)
    {
        try { await fl.MoveClipAsync(clipIndex, startTick, track, ct); return $"Moved clip {clipIndex} to tick {startTick}{(track >= 0 ? $" track {track}" : "")}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_resize_clip")]
    [Description("Resize a playlist clip (slot index) to a new length in ticks.")]
    public async Task<string> ResizeClipAsync(
        [Description("Clip slot index")] int clipIndex,
        [Description("New length, ticks")] int lengthTick, CancellationToken ct = default)
    {
        try { await fl.ResizeClipAsync(clipIndex, lengthTick, ct); return $"Resized clip {clipIndex} to {lengthTick} ticks."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_delete_clip")]
    [Description("Delete a playlist clip by slot index (best-effort: marks slot inactive).")]
    public async Task<string> DeleteClipAsync(
        [Description("Clip slot index")] int clipIndex, CancellationToken ct = default)
    {
        try { await fl.DeleteClipAsync(clipIndex, ct); return $"Deleted clip {clipIndex}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_mute_clip")]
    [Description("Mute/unmute a playlist clip by slot index (from native_list_clips).")]
    public async Task<string> MuteClipAsync(
        [Description("Clip slot index")] int clipIndex,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default)
    {
        try { await fl.SetClipMutedAsync(clipIndex, muted, ct); return $"Clip {clipIndex} {(muted ? "muted" : "unmuted")}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_slice_clip")]
    [Description("Slice/chop a playlist clip into two at an absolute tick (split must be inside the clip). Pattern + audio clips; audio playback stays continuous.")]
    public async Task<string> SliceClipAsync(
        [Description("Clip slot index")] int clipIndex,
        [Description("Absolute tick to cut (inside the clip)")] int tick, CancellationToken ct = default)
    {
        try { await fl.SliceClipAsync(clipIndex, tick, ct); return $"Sliced clip {clipIndex} at tick {tick}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_duplicate_clip")]
    [Description("Duplicate a playlist clip; copy goes right after it on the same track.")]
    public async Task<string> DuplicateClipAsync(
        [Description("Clip slot index")] int clipIndex, CancellationToken ct = default)
    {
        try { await fl.DuplicateClipAsync(clipIndex, ct); return $"Duplicated clip {clipIndex}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Song / transport state ----------------

    [KernelFunction("native_get_song_state")]
    [Description("Read playback context: playhead (bar/beat/tick), song-vs-pattern mode, play state, loop region, song length.")]
    public async Task<string> GetSongStateAsync(CancellationToken ct = default)
    {
        try { return await fl.GetSongStateAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_set_song_mode")]
    [Description("Switch song mode (full arrangement) vs pattern mode. song=true -> song mode.")]
    public async Task<string> SetSongModeAsync(
        [Description("true = song mode, false = pattern mode")] bool song, CancellationToken ct = default)
    {
        try { await fl.SetSongModeAsync(song, ct); return $"Switched to {(song ? "song" : "pattern")} mode."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_seek")]
    [Description("Move song playhead to an absolute tick (PPQ; native_get_ppq ticks/quarter). Clamps to song length.")]
    public async Task<string> SeekAsync(
        [Description("Absolute tick")] int tick, CancellationToken ct = default)
    {
        try { await fl.SeekAsync(tick, ct); return $"Playhead moved to tick {tick}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_list_markers")]
    [Description("List timeline markers (name + tick).")]
    public async Task<string> ListMarkersAsync(CancellationToken ct = default)
    {
        try { return await fl.ListMarkersAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_marker")]
    [Description("Add a timeline marker at a tick with a name (e.g. 'Verse','Chorus','Drop').")]
    public async Task<string> AddMarkerAsync(
        [Description("Tick")] int tick,
        [Description("Marker name")] string name, CancellationToken ct = default)
    {
        try { await fl.AddMarkerAsync(tick, name, ct); return $"Added marker '{name}' at tick {tick}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Project lifecycle ----------------

    [KernelFunction("native_save_project")]
    [Description("Save current FL project. Pass an absolute .flp path to save-as, or empty to save to the current file (fails if never saved). Affects user's project — confirm first.")]
    public async Task<string> SaveProjectAsync(
        [Description("Absolute .flp path, or empty = current file")] string path = "", CancellationToken ct = default)
    {
        try { await fl.SaveProjectAsync(path, ct); return string.IsNullOrWhiteSpace(path) ? "Saved project." : $"Saved project to {path}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_open_project")]
    [Description("Open an FL project (.flp) by absolute path, REPLACING current (unsaved changes lost). Confirm first.")]
    public async Task<string> OpenProjectAsync(
        [Description("Absolute .flp path")] string path, CancellationToken ct = default)
    {
        try { await fl.OpenProjectAsync(path, ct); return $"Opened {path}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_new_project")]
    [Description("Start a new empty FL project, REPLACING current (unsaved changes lost). Confirm first.")]
    public async Task<string> NewProjectAsync(CancellationToken ct = default)
    {
        try { await fl.NewProjectAsync(ct); return "Started a new project."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_get_project_info")]
    [Description("Report current project title, file path, and saved-or-untitled.")]
    public async Task<string> GetProjectInfoAsync(CancellationToken ct = default)
    {
        try { return await fl.GetProjectInfoAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_save_project_as")]
    [Description("Save As: save to a new absolute .flp path AND make it current (updates title + recent files). Affects user's project — confirm first.")]
    public async Task<string> SaveProjectAsAsync(
        [Description("Absolute .flp path")] string path, CancellationToken ct = default)
    {
        try { await fl.SaveProjectAsAsync(path, ct); return $"Saved as {path} (now the current project)."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_save_copy")]
    [Description("Save a COPY to an absolute .flp path WITHOUT changing the current project/title (backup/export). Safe — doesn't affect the open project's save state.")]
    public async Task<string> SaveCopyAsync(
        [Description("Absolute .flp path for the copy")] string path, CancellationToken ct = default)
    {
        try { await fl.SaveCopyAsync(path, ct); return $"Saved a copy to {path}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_save_new_version")]
    [Description("Save an auto-incremented new version (e.g. song_2.flp, song_3.flp) and make it current. Requires the project saved at least once.")]
    public async Task<string> SaveNewVersionAsync(CancellationToken ct = default)
    {
        try { await fl.SaveNewVersionAsync(ct); return "Saved a new version."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_list_recent_projects")]
    [Description("List recently-opened FL projects (most recent first).")]
    public async Task<string> ListRecentProjectsAsync(CancellationToken ct = default)
    {
        try { return await fl.ListRecentProjectsAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Arrangements ----------------

    [KernelFunction("native_list_arrangements")]
    [Description("List arrangements (current marked *), with indices.")]
    public async Task<string> ListArrangementsAsync(CancellationToken ct = default)
    {
        try { return await fl.ListArrangementsAsync(ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_make_arrangement")]
    [Description("Create a new empty arrangement + switch to it. Optionally name. Returns new index.")]
    public async Task<string> MakeArrangementAsync(
        [Description("Optional name; empty = FL default")] string name = "", CancellationToken ct = default)
    {
        try { int i = await fl.AddArrangementAsync(string.IsNullOrWhiteSpace(name) ? null : name, ct); return $"Created arrangement {i}{(string.IsNullOrWhiteSpace(name) ? "" : $" '{name}'")}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_clone_arrangement")]
    [Description("Clone an arrangement — deep-copy tracks + playlist clips into a new arrangement + switch to it. srcIndex < 0 = current. Optionally name.")]
    public async Task<string> CloneArrangementAsync(
        [Description("Source index, or -1 = current")] int srcIndex = -1,
        [Description("Optional name for the clone")] string name = "", CancellationToken ct = default)
    {
        try { int i = await fl.CloneArrangementAsync(srcIndex, string.IsNullOrWhiteSpace(name) ? null : name, ct); return $"Cloned to arrangement {i}{(string.IsNullOrWhiteSpace(name) ? "" : $" '{name}'")}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_rename_arrangement")]
    [Description("Rename an arrangement by index. (Shows immediately; persistence across save/reload not guaranteed yet.)")]
    public async Task<string> RenameArrangementAsync(
        [Description("Arrangement index")] int index,
        [Description("New name")] string name, CancellationToken ct = default)
    {
        try { await fl.RenameArrangementAsync(index, name, ct); return $"Renamed arrangement {index} to '{name}'."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_delete_arrangement")]
    [Description("Delete an arrangement by index. (Headless-safe: bridge suppresses FL's blocking autosave.)")]
    public async Task<string> DeleteArrangementAsync(
        [Description("Arrangement index")] int index, CancellationToken ct = default)
    {
        try { await fl.DeleteArrangementAsync(index, ct); return $"Deleted arrangement {index}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_select_arrangement")]
    [Description("Switch to (make current) the arrangement at index.")]
    public async Task<string> SelectArrangementAsync(
        [Description("Arrangement index")] int index, CancellationToken ct = default)
    {
        try { await fl.SelectArrangementAsync(index, ct); return $"Switched to arrangement {index}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // ---------------- Automation clips ----------------

    [KernelFunction("native_list_automation_points")]
    [Description("List an automation-clip channel's points (time in beats, value 0-1, tension, curve). Channel must host the Automation Clip generator (create via native_add_channel(\"Automation Clip\")).")]
    public async Task<string> ListAutomationPointsAsync(
        [Description("Automation-clip channel index")] int channel, CancellationToken ct = default)
    {
        try { return await fl.ListAutomationPointsAsync(channel, ct); }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_add_automation_point")]
    [Description("Add a point to an automation-clip channel: time in BEATS (4 beats = 1 bar), value 0.0-1.0, tension -1.0..1.0 (0=linear). Inserts in time order + rebuilds curve.")]
    public async Task<string> AddAutomationPointAsync(
        [Description("Automation-clip channel index")] int channel,
        [Description("Time in beats")] double timeBeats,
        [Description("Value 0.0-1.0")] double value,
        [Description("Tension -1.0..1.0 (0=linear)")] double tension = 0, CancellationToken ct = default)
    {
        try { await fl.AddAutomationPointAsync(channel, timeBeats, value, tension, ct); return $"Added automation point at beat {timeBeats:0.###} = {value:0.###}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    [KernelFunction("native_delete_automation_point")]
    [Description("Delete an automation point by index from an automation-clip channel.")]
    public async Task<string> DeleteAutomationPointAsync(
        [Description("Automation-clip channel index")] int channel,
        [Description("Point index")] int index, CancellationToken ct = default)
    {
        try { await fl.DeleteAutomationPointAsync(channel, index, ct); return $"Deleted automation point {index}."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }

    // native_render is intentionally NOT a [KernelFunction]: FLproj_FileExportFormat opens a MODAL
    // export dialog that runs a nested message loop on FL's main thread, which STOPS playback and
    // blocks the main thread until the user dismisses it. Letting the agent trigger that unprompted
    // mid-task is disruptive (and was a latent freeze class before OpenExportDialogAsync was made
    // non-blocking). Export stays a user-initiated action; this method is retained for an explicit
    // user "export" intent invoked from the UI, and OpenExportDialogAsync is now non-blocking so even
    // that path can never wedge the bridge. Re-expose only behind an explicit user-intent gate.
    public async Task<string> RenderAsync(CancellationToken ct = default)
    {
        try { await fl.OpenExportDialogAsync(0, ct); return "Opened FL's export dialog — choose a format/path and click Render."; }
        catch (Exception ex) { return PluginSupport.BridgeError(ex); }
    }
}
