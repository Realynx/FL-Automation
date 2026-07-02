using System.ComponentModel;
using System.IO;
using FruityLink.Core.Abstractions;
using ModelContextProtocol.Server;

namespace FruityLink.Mcp.Tools;

/// <summary>Plugins / inserts: enumerate, add channel generators, manage mixer FX slots.</summary>
[McpServerToolType]
public sealed class PluginInsertTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_available_plugins", ReadOnly = true)]
    [Description("List installed plugins addable as inserts. kind = 'generator' (channel instruments) or 'effect' (mixer FX). Use the returned names with native_add_channel / native_add_mixer_effect.")]
    public Task<string> ListAvailablePlugins([Description("'generator' or 'effect'")] string kind, CancellationToken ct = default) =>
        McpSupport.SafeAsync(() =>
        {
            bool effects = kind?.Trim().ToLowerInvariant() is "effect" or "effects" or "fx";
            return fl.ListAvailablePluginsAsync(effects, ct);
        });

    [McpServerTool(Name = "native_get_channel_plugin", ReadOnly = true)]
    [Description("Report a channel's loaded generator plugin (if any).")]
    public Task<string> GetChannelPlugin([Description("Channel index (0-based)")] int channel, CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.GetChannelPluginAsync(channel, ct));

    [McpServerTool(Name = "native_add_channel")]
    [Description("Add a channel hosting the named generator/instrument (name from native_list_available_plugins kind='generator', e.g. 'Sytrus','FLEX','BooBass'). Returns the new channel index. Use 'Automation Clip' to create an automation-clip channel.")]
    public Task<string> AddChannel([Description("Generator/instrument name")] string plugin, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { int i = await fl.AddChannelAsync(plugin, ct); return $"Added channel {i} with '{plugin}'."; });

    [McpServerTool(Name = "native_list_mixer_effects", ReadOnly = true)]
    [Description("List the effects loaded in a mixer track's 10 FX slots.")]
    public Task<string> ListMixerEffects([Description("Mixer track")] int track, CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListMixerEffectsAsync(track, ct));

    [McpServerTool(Name = "native_add_mixer_effect")]
    [Description("Load the named effect into a mixer FX slot (0-9). Name from native_list_available_plugins kind='effect' (e.g. 'Fruity Reeverb 2','Fruity Limiter','Fruity Parametric EQ 2').")]
    public Task<string> AddMixerEffect(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Effect name")] string plugin, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.AddMixerEffectAsync(track, slot, plugin, ct); return $"Loaded '{plugin}' into mixer track {track}, slot {slot}."; });

    [McpServerTool(Name = "native_remove_mixer_effect")]
    [Description("Clear (remove the effect from) a mixer track's FX slot.")]
    public Task<string> RemoveMixerEffect(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.RemoveMixerEffectAsync(track, slot, ct); return $"Cleared mixer track {track}, slot {slot}."; });

    [McpServerTool(Name = "native_clone_mixer_effect")]
    [Description("Copy the effect TYPE between FX slots on the same mixer track (type only, not parameter state).")]
    public Task<string> CloneMixerEffect(
        [Description("Mixer track")] int track,
        [Description("Source FX slot")] int fromSlot,
        [Description("Destination FX slot")] int toSlot, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.CloneMixerEffectAsync(track, fromSlot, toSlot, ct); return $"Copied effect from slot {fromSlot} to slot {toSlot} on track {track}."; });
}

/// <summary>Plugin parameters (VST / native sound design) for channel generators and mixer FX.</summary>
[McpServerToolType]
public sealed class PluginParamTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_channel_plugin_params", ReadOnly = true)]
    [Description("List a channel generator's parameters as 'index: name = value'. Optionally filter by name substring (recommended for big synths like Serum, ~1000 params). Use the index with native_set_channel_plugin_param.")]
    public Task<string> ListChannelPluginParams(
        [Description("Channel index (0-based)")] int channel,
        [Description("Optional name filter (e.g. 'filter','osc','cutoff'); empty = all")] string filter = "",
        CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListPluginParamsAsync(channel, -1, string.IsNullOrWhiteSpace(filter) ? null : filter, ct));

    [McpServerTool(Name = "native_set_channel_plugin_param")]
    [Description("Set a channel generator parameter to a normalized value 0.0-1.0 (e.g. Serum filter cutoff). Index from native_list_channel_plugin_params.")]
    public Task<string> SetChannelPluginParam(
        [Description("Channel index (0-based)")] int channel,
        [Description("Parameter index")] int paramIndex,
        [Description("Normalized value 0.0-1.0")] double value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetPluginParamAsync(channel, -1, paramIndex, value, ct); return $"Channel {channel} param {paramIndex} set to {value:0.###}."; });

    [McpServerTool(Name = "native_list_mixer_plugin_params", ReadOnly = true)]
    [Description("List a mixer FX-slot effect's parameters as 'index: name = value'. Optionally filter by name substring. Use the index with native_set_mixer_plugin_param.")]
    public Task<string> ListMixerPluginParams(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Optional name filter; empty = all")] string filter = "",
        CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListPluginParamsAsync(track, slot, string.IsNullOrWhiteSpace(filter) ? null : filter, ct));

    [McpServerTool(Name = "native_set_mixer_plugin_param")]
    [Description("Set a mixer FX-slot parameter to a normalized value 0.0-1.0 (e.g. a Pro-Q band freq/gain). Index from native_list_mixer_plugin_params.")]
    public Task<string> SetMixerPluginParam(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Parameter index")] int paramIndex,
        [Description("Normalized value 0.0-1.0")] double value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetPluginParamAsync(track, slot, paramIndex, value, ct); return $"Track {track} slot {slot} param {paramIndex} set to {value:0.###}."; });
}

/// <summary>Audio samples: browse, add as a channel, replace a channel's sample.</summary>
[McpServerToolType]
public sealed class SampleTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_list_samples", ReadOnly = true)]
    [Description("List audio samples (FL factory packs + user content) for drums/one-shots/loops. Filter by name/path substring (e.g. 'kick','snare','808','vocal'). Use a returned full path with native_add_sample_channel.")]
    public Task<string> ListSamples(
        [Description("Optional name/path filter; empty = broad list")] string filter = "",
        CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListSamplesAsync(string.IsNullOrWhiteSpace(filter) ? null : filter, ct));

    [McpServerTool(Name = "native_add_sample_channel")]
    [Description("Add a channel that plays the given audio sample (drum/one-shot/loop). Pass a full path (from native_list_samples). Returns the new channel index.")]
    public Task<string> AddSampleChannel(
        [Description("Full path to a .wav/.mp3/.flac/.ogg/.aiff sample")] string samplePath, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { int i = await fl.AddSampleChannelAsync(samplePath, ct); return $"Added sample channel {i}: {Path.GetFileName(samplePath)}."; });

    [McpServerTool(Name = "native_replace_channel_sample")]
    [Description("Replace a channel's sample with another audio file (full path).")]
    public Task<string> ReplaceChannelSample(
        [Description("Channel index (0-based)")] int channel,
        [Description("Full path to the new sample")] string samplePath, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.ReplaceChannelSampleAsync(channel, samplePath, ct); return $"Replaced channel {channel} sample with {Path.GetFileName(samplePath)}."; });
}
