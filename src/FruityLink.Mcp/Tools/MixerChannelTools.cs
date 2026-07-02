using System.ComponentModel;
using FruityLink.Core.Abstractions;
using ModelContextProtocol.Server;

namespace FruityLink.Mcp.Tools;

/// <summary>Mixer: track volume/pan, FX-slot params, sends, EQ.</summary>
[McpServerToolType]
public sealed class MixerTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_set_mixer_volume")]
    [Description("Set a mixer track volume, 0-12800 (track 0 = master).")]
    public Task<string> SetMixerVolume(
        [Description("Mixer track (0 = master)")] int track,
        [Description("0-12800")] int value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetMixerVolumeAsync(track, value, ct); return $"Mixer track {track} volume = {value}."; });

    [McpServerTool(Name = "native_set_mixer_pan")]
    [Description("Set a mixer track pan, 0-12800 (6400 = center).")]
    public Task<string> SetMixerPan(
        [Description("Mixer track")] int track,
        [Description("0-12800, 6400 = center")] int value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetMixerPanAsync(track, value, ct); return $"Mixer track {track} pan = {value}."; });

    [McpServerTool(Name = "native_set_mixer_fx_param")]
    [Description("Set a parameter of a plugin in a mixer FX slot using a raw normalized fixed-point value. For a normalized 0.0-1.0 value, prefer native_set_mixer_plugin_param.")]
    public Task<string> SetMixerFxParam(
        [Description("Mixer track")] int track,
        [Description("FX slot 0-9")] int slot,
        [Description("Parameter index")] int paramIndex,
        [Description("Normalized fixed-point value")] long value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetMixerFxParamAsync(track, slot, paramIndex, value, ct); return $"Mixer {track} slot {slot} param {paramIndex} set."; });

    [McpServerTool(Name = "native_set_mixer_send")]
    [Description("Set a mixer send srcTrack->dstTrack at a level (0.0-1.25, 1.0 = unity). Enables the route and sets the send level.")]
    public Task<string> SetMixerSend(
        [Description("Source mixer track")] int srcTrack,
        [Description("Destination mixer track")] int dstTrack,
        [Description("Send level (1.0 = unity)")] double level, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetMixerSendAsync(srcTrack, dstTrack, level, ct); return $"Mixer send {srcTrack}->{dstTrack} set to {level:0.###}."; });

    [McpServerTool(Name = "native_set_mixer_eq_gain")]
    [Description("Set a mixer track's built-in EQ band gain. band: 0=low, 1=mid, 2=high. value 0-1073741824 (~536870912 = 0 dB).")]
    public Task<string> SetMixerEqGain(
        [Description("Mixer track")] int track,
        [Description("EQ band: 0=low, 1=mid, 2=high")] int band,
        [Description("Raw gain 0-1073741824 (~536870912 = 0 dB)")] int value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetMixerEqGainAsync(track, band, value, ct); return $"Mixer track {track} EQ band {band} gain set."; });
}

/// <summary>Channel rack: per-channel volume/pan/pitch/mute/route, listing and selection.</summary>
[McpServerToolType]
public sealed class ChannelTools(INativeFlControl fl)
{
    [McpServerTool(Name = "native_set_channel_volume")]
    [Description("Set a channel-rack channel volume, 0-12800 (10000 = default 78%).")]
    public Task<string> SetChannelVolume(
        [Description("Channel index (0-based)")] int channel,
        [Description("0-12800")] int value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetChannelVolumeAsync(channel, value, ct); return $"Channel {channel} volume = {value}."; });

    [McpServerTool(Name = "native_set_channel_pan")]
    [Description("Set a channel pan, 0-12800 (6400 = center).")]
    public Task<string> SetChannelPan(
        [Description("Channel index (0-based)")] int channel,
        [Description("0-12800, 6400 = center")] int value, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetChannelPanAsync(channel, value, ct); return $"Channel {channel} pan = {value}."; });

    [McpServerTool(Name = "native_set_channel_pitch")]
    [Description("Set a channel pitch in cents (0 = center).")]
    public Task<string> SetChannelPitch(
        [Description("Channel index (0-based)")] int channel,
        [Description("cents (0 = center)")] int cents, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetChannelPitchAsync(channel, cents, ct); return $"Channel {channel} pitch = {cents} cents."; });

    [McpServerTool(Name = "native_set_channel_muted")]
    [Description("Mute or unmute a channel.")]
    public Task<string> SetChannelMuted(
        [Description("Channel index (0-based)")] int channel,
        [Description("true = mute, false = unmute")] bool muted, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetChannelMutedAsync(channel, muted, ct); return $"Channel {channel} {(muted ? "muted" : "unmuted")}."; });

    [McpServerTool(Name = "native_route_channel_to_mixer")]
    [Description("Route a channel to a mixer track (0-125).")]
    public Task<string> RouteChannelToMixer(
        [Description("Channel index (0-based)")] int channel,
        [Description("Mixer track 0-125")] int mixerTrack, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SetChannelFxRouteAsync(channel, mixerTrack, ct); return $"Channel {channel} routed to mixer track {mixerTrack}."; });

    [McpServerTool(Name = "native_list_channels", ReadOnly = true)]
    [Description("List channel-rack channels as 'index: name'. Use the index with note/parameter tools.")]
    public Task<string> ListChannels(CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.ListChannelsAsync(ct));

    [McpServerTool(Name = "native_get_channel_count", ReadOnly = true)]
    [Description("Get the number of channel-rack channels.")]
    public Task<string> GetChannelCount(CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => $"{await fl.GetChannelCountAsync(ct)} channel(s)");

    [McpServerTool(Name = "native_get_channel_name", ReadOnly = true)]
    [Description("Get a single channel's display name by index (0-based).")]
    public Task<string> GetChannelName([Description("Channel index (0-based)")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(() => fl.GetChannelNameAsync(index, ct));

    [McpServerTool(Name = "native_select_channel")]
    [Description("Exclusively select a channel (0-based) so the piano roll edits it.")]
    public Task<string> SelectChannel([Description("Channel index (0-based)")] int index, CancellationToken ct = default) =>
        McpSupport.SafeAsync(async () => { await fl.SelectChannelAsync(index, ct); return $"Selected channel {index}."; });
}
