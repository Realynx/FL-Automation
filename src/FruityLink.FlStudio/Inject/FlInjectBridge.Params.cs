using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Command-bus GET/SET param protocol: tempo/master/shuffle globals, mixer + channel param ids.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ---- typed control ops (grows as the RE catalog lands) -------------------

    /// <summary>FL_DispatchCommand(cmdId, value, flags) on the main thread.</summary>
    public Task DispatchCommandAsync(uint cmdId, ulong value, uint flags = FlagWheelOrScript, CancellationToken ct = default)
        => CallAsync(CmdBusAddr, new ulong[] { cmdId, value, flags }, ct);

    /// <summary>Read a parameter's current value via the bus GET protocol (flags 0x2). Live-verified.</summary>
    public async Task<long> GetParamAsync(uint cmdId, CancellationToken ct = default)
        => (long)await CallAsync(CmdBusAddr, new ulong[] { cmdId, 0, FlagGet }, ct);

    /// <summary>Set a parameter's value via the bus SET protocol (flags 0x11). Live-verified.</summary>
    public Task SetParamAsync(uint cmdId, long value, CancellationToken ct = default)
        => DispatchCommandAsync(cmdId, unchecked((ulong)value), FlagSet, ct);

    /// <summary>Set project tempo in BPM (10..522). Live-verified.</summary>
    public Task SetTempoAsync(double bpm, CancellationToken ct = default)
        => SetParamAsync(CmdSetTempo, (long)Math.Round(Math.Clamp(bpm, 10.0, 522.0) * 1000.0), ct);

    /// <summary>Get project tempo in BPM.</summary>
    public async Task<double> GetTempoAsync(CancellationToken ct = default)
        => await GetParamAsync(CmdSetTempo, ct) / 1000.0;

    /// <summary>Master volume, 0..12800 (≈7624 default). Live-verified.</summary>
    public Task SetMasterVolumeAsync(int value, CancellationToken ct = default)
        => SetParamAsync(CmdMasterVolume, Math.Clamp(value, 0, 12800), ct);

    /// <summary>Read master volume, 0..12800 (bus GET; symmetric with the setter).</summary>
    public async Task<int> GetMasterVolumeAsync(CancellationToken ct = default)
        => (int)await GetParamAsync(CmdMasterVolume, ct);

    /// <summary>Master pitch in cents, -1200..+1200. Live-verified.</summary>
    public Task SetMasterPitchAsync(int cents, CancellationToken ct = default)
        => SetParamAsync(CmdMasterPitch, Math.Clamp(cents, -1200, 1200), ct);

    /// <summary>Read master pitch in cents (bus GET; low-32-bit signed, so negative cents round-trip).</summary>
    public async Task<int> GetMasterPitchAsync(CancellationToken ct = default)
        => unchecked((int)await GetParamAsync(CmdMasterPitch, ct));

    /// <summary>Global shuffle/swing, 0..128. Live-verified.</summary>
    public Task SetShuffleAsync(int value, CancellationToken ct = default)
        => SetParamAsync(CmdShuffle, Math.Clamp(value, 0, 128), ct);

    /// <summary>Read global shuffle/swing, 0..128 (bus GET; symmetric with the setter).</summary>
    public async Task<int> GetShuffleAsync(CancellationToken ct = default)
        => (int)await GetParamAsync(CmdShuffle, ct);

    // ---- mixer (param-id protocol; track volume live-verified) ----------------
    // base(track,slot) = (track*0x40 + slot) << 16. Track controls live in slot 0's namespace.
    public const uint MixerVolOffset = 0x70001FC0, MixerPanOffset = 0x70001FC1, MixerStereoSepOffset = 0x70001FC2;

    /// <summary>Mixer track-control param id (track 0 = master); add a control offset to the track base.</summary>
    public static uint MixerTrackParamId(int track, uint controlOffset) => (uint)((track * 0x40) << 16) + controlOffset;

    /// <summary>Mixer FX-slot plugin param id (paramIndex within an FX slot).</summary>
    public static uint MixerFxParamId(int track, int slot, int paramIndex)
        => (uint)(((track * 0x40 + slot) << 16) + paramIndex) + 0x70008000u;

    /// <summary>Set a mixer track volume 0..12800 (track 0 = master). Live-verified.</summary>
    public Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default)
        => SetParamAsync(MixerTrackParamId(track, MixerVolOffset), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Read a mixer track volume 0..12800.</summary>
    public Task<long> GetMixerVolumeAsync(int track, CancellationToken ct = default)
        => GetParamAsync(MixerTrackParamId(track, MixerVolOffset), ct);

    /// <summary>Set a mixer track pan 0..12800 (6400 = center).</summary>
    public Task SetMixerPanAsync(int track, int value, CancellationToken ct = default)
        => SetParamAsync(MixerTrackParamId(track, MixerPanOffset), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Read a mixer track pan 0..12800 (bus GET; symmetric with the setter).</summary>
    public async Task<int> GetMixerPanAsync(int track, CancellationToken ct = default)
        => (int)await GetParamAsync(MixerTrackParamId(track, MixerPanOffset), ct);

    /// <summary>Set a mixer FX-slot plugin parameter (normalized fixed-point value).</summary>
    public Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default)
        => SetParamAsync(MixerFxParamId(track, slot, paramIndex), value, ct);

    // ---- channel rack (cmd = (channel<<16) + paramIndex; recTag==index for normal projects) -----
    // Live-verified: vol/pan/pitch/mute on channels 0..3.
    public const uint ChanVol = 0, ChanPan = 1, ChanPitch = 4, ChanMute = 7, ChanFxRoute = 8;

    /// <summary>Channel param id. NOTE: assumes recTag==channel index (true for unreordered projects);
    /// for reordered/deleted-channel projects, resolve the channel's recEventId first.</summary>
    public static uint ChannelParamId(int channel, uint paramIndex) => (uint)(channel << 16) + paramIndex;

    /// <summary>Set channel volume 0..12800 (10000 = default 78%). Live-verified.</summary>
    public Task SetChannelVolumeAsync(int channel, int value, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanVol), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Get channel volume 0..12800.</summary>
    public Task<long> GetChannelVolumeAsync(int channel, CancellationToken ct = default)
        => GetParamAsync(ChannelParamId(channel, ChanVol), ct);

    /// <summary>Set channel pan 0..12800 (6400 = center). Live-verified.</summary>
    public Task SetChannelPanAsync(int channel, int value, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanPan), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Read channel pan 0..12800 (bus GET; symmetric with the setter).</summary>
    public async Task<int> GetChannelPanAsync(int channel, CancellationToken ct = default)
        => (int)await GetParamAsync(ChannelParamId(channel, ChanPan), ct);

    /// <summary>Set channel pitch in cents (0 = center). Live-verified.</summary>
    public Task SetChannelPitchAsync(int channel, int cents, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanPitch), cents, ct);

    /// <summary>Read channel pitch in cents (bus GET; low-32-bit signed so negative cents round-trip).</summary>
    public async Task<int> GetChannelPitchAsync(int channel, CancellationToken ct = default)
        => unchecked((int)await GetParamAsync(ChannelParamId(channel, ChanPitch), ct));

    /// <summary>Mute/unmute a channel (engine "enabled" flag: 1=unmuted). Live-verified.</summary>
    public Task SetChannelMutedAsync(int channel, bool muted, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanMute), muted ? 0 : 1, ct);

    /// <summary>Read a channel's mute state (bus GET of the "enabled" flag: 0 = muted).</summary>
    public async Task<bool> GetChannelMutedAsync(int channel, CancellationToken ct = default)
        => await GetParamAsync(ChannelParamId(channel, ChanMute), ct) == 0;

    /// <summary>Route a channel to a mixer track (0..125). </summary>
    public Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanFxRoute), Math.Clamp(mixerTrack, 0, 500), ct);

    /// <summary>Read a channel's mixer-track route (bus GET; symmetric with the setter).</summary>
    public async Task<int> GetChannelFxRouteAsync(int channel, CancellationToken ct = default)
        => (int)await GetParamAsync(ChannelParamId(channel, ChanFxRoute), ct);
}
