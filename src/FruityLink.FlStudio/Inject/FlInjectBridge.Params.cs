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

    /// <summary>Master volume as a raw native integer, 0..12800. No dB conversion is defined. Live-verified.</summary>
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

    /// <summary>FL's mixer fader range (the strip slider's native range is 16000 with default 0x3200 = 12800 = 0 dB;
    /// re/ui-gap-mixer-strips.md). Earlier SDK builds clamped at 12800 and could not reach the +5.6 dB headroom.</summary>
    public const int MixerVolumeMax = 16000, MixerVolumeUnity = 12800;

    /// <summary>Set mixer track volume as a raw native integer, 0..16000 (track 0 = master; 12800 = 0 dB). Live-verified up to 12800; the 12801..16000 headroom is clamped no further than FL's own fader range.</summary>
    public async Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default)
    {
        await ValidateAddressableMixerTrackAsync(track, ct);
        await SetParamAsync(MixerTrackParamId(track, MixerVolOffset), Math.Clamp(value, 0, MixerVolumeMax), ct);
    }

    /// <summary>Read a mixer track volume 0..16000 (12800 = 0 dB).</summary>
    public async Task<long> GetMixerVolumeAsync(int track, CancellationToken ct = default)
    {
        await ValidateAddressableMixerTrackAsync(track, ct);
        return await GetParamAsync(MixerTrackParamId(track, MixerVolOffset), ct);
    }

    /// <summary>Mixer pan magnitude at hard left/right. The mixer pan bus value is SIGNED with 0 = center,
    /// unlike channel pan (0..12800, 6400 = center). Live evidence (Ember Tides v006): untouched tracks
    /// read 0, and a stored 6800 rendered with a silent left channel; 0 rendered L/R within 0.04 dB.</summary>
    public const int MixerPanLimit = 6400;

    /// <summary>Set a mixer track pan -6400..6400 (0 = center, negative = left). Values outside are clamped.</summary>
    public async Task SetMixerPanAsync(int track, int value, CancellationToken ct = default)
    {
        await ValidateAddressableMixerTrackAsync(track, ct);
        await SetParamAsync(MixerTrackParamId(track, MixerPanOffset), Math.Clamp(value, -MixerPanLimit, MixerPanLimit), ct);
    }

    /// <summary>Read a mixer track pan -6400..6400 (bus GET; low-32-bit signed so left values round-trip).</summary>
    public async Task<int> GetMixerPanAsync(int track, CancellationToken ct = default)
    {
        await ValidateAddressableMixerTrackAsync(track, ct);
        return unchecked((int)await GetParamAsync(MixerTrackParamId(track, MixerPanOffset), ct));
    }

    /// <summary>Set a mixer FX-slot plugin parameter (normalized fixed-point value).</summary>
    public async Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default)
    {
        await ValidateAddressableMixerTrackAsync(track, ct);
        await SetParamAsync(MixerFxParamId(track, slot, paramIndex), value, ct);
    }

    // ---- channel rack (cmd = (channel<<16) + paramIndex; recTag==index for normal projects) -----
    // Live-verified: vol/pan/pitch/mute on channels 0..3.
    public const uint ChanVol = 0, ChanPan = 1, ChanPitch = 4, ChanMute = 7, ChanFxRoute = 8;
    // FL SDK REC_Chan table entries NOT yet live-verified (kept as the harvest targets for the channel-control ops):
    // 2 FCut, 3 FRes, 5 PanDelay, 6 FType, 9 GateTime, 10 Crossfade, 11 TimeOfs, 12 SwingMix, 13 SmpOffset, 14 StretchTime.
    public const uint ChanSmpOffset = 13, ChanStretchTime = 14;
    /// <summary>Highest REC_Chan control index the generic channel-control ops accept (below the plugin-parameter block).</summary>
    public const int ChanControlMax = 0x1FFF;

    /// <summary>Channel param id. NOTE: assumes recTag==channel index (true for unreordered projects);
    /// for reordered/deleted-channel projects, resolve the channel's recEventId first.</summary>
    public static uint ChannelParamId(int channel, uint paramIndex) => (uint)(channel << 16) + paramIndex;

    /// <summary>Set channel volume as a raw native integer, 0..12800. Live-verified.</summary>
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

    /// <summary>Route a channel to an existing Master or ordinary mixer insert.</summary>
    public async Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default)
    {
        await ValidateAddressableMixerTrackAsync(mixerTrack, ct);
        await SetParamAsync(ChannelParamId(channel, ChanFxRoute), mixerTrack, ct);
    }

    /// <summary>Read a channel's mixer-track route (bus GET; symmetric with the setter).</summary>
    public async Task<int> GetChannelFxRouteAsync(int channel, CancellationToken ct = default)
        => (int)await GetParamAsync(ChannelParamId(channel, ChanFxRoute), ct);

    private static uint CheckedChannelControl(int channel, int control)
    {
        if (channel < 0) throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be a zero-based rack index.");
        if (control < 0 || control > ChanControlMax)
            throw new ArgumentOutOfRangeException(nameof(control), $"Channel control must be a REC_Chan event index 0..{ChanControlMax}; hosted-plugin parameters use set_plugin_param.");
        return ChannelParamId(channel, (uint)control);
    }

    /// <summary>Read a built-in channel control by REC_Chan index (bus GET, raw native int; low 32 bits signed so
    /// negative controls such as pitch round-trip). Same protocol as the live-verified volume/pan/pitch/mute/route reads.</summary>
    public async Task<long> GetChannelControlAsync(int channel, int control, CancellationToken ct = default)
        => unchecked((int)await GetParamAsync(CheckedChannelControl(channel, control), ct));

    /// <summary>Write a built-in channel control by REC_Chan index (bus SET, raw native int). Not clamped: each
    /// control has its own native range, so read the current value first and confirm in the Channel settings window.</summary>
    public Task SetChannelControlAsync(int channel, int control, long value, CancellationToken ct = default)
    {
        uint id = CheckedChannelControl(channel, control);
        if (value is < int.MinValue or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value), "Channel control values are 32-bit native integers.");
        LogOp("SetChannelControl", $"channel={channel} control={control} value={value}");
        return SetParamAsync(id, value, ct);
    }
}
