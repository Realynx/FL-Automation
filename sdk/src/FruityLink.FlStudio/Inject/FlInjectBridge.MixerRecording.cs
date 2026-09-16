using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Mixer disk recording: the per-insert record-arm state that live audio capture relies on.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
//
// Harvested 2026-09-14 from FL's own scripting API (read-only Ghidra pass on 26.1.3.5570 and 25.2.5.5319;
// native/bridge/analysis/verified-symbols-arm-2026-09-14.json): `mixer.isTrackArmed` reads the byte at
// trackStruct + MixerTrackArmedOffset (0x1470 / 0x145C), and `mixer.armTrack` marshals to a main-thread
// callback that calls the setter thunk FLmx_SetTrackArmed(RCX = trackStruct, DL = armed, R8 = 0) only when
// the byte differs. Both symbols decode from one unique signature over that callback, so they are refused
// together on a build where it does not match. The setter forwards to the track's recorder object, which
// writes the byte, names the recording (auto-name or dialog per FL's settings) and refreshes the mixer.
public sealed partial class FlInjectBridge
{
    private const string SetTrackArmedSymbol = "sym:FLmx_SetTrackArmed";
    private const string ArmedOffsetSymbol = "MixerTrackArmedOffset";

    private async Task<(ulong Struct, ulong Offset)> ArmedFieldAsync(int track, CancellationToken ct)
    {
        var layout = await MixerLayoutAsync(ct);
        ulong trackStruct = await MixerTrackStructAsync(track, layout, ct);
        if (trackStruct == 0) throw new InvalidOperationException($"Mixer track {track} not available.");
        ulong offset = await ResolveSymbolAddressAsync(ArmedOffsetSymbol, ct);
        if (offset + 1 > (ulong)layout.TrackStride)
            throw new InvalidOperationException($"Mixer armed-byte offset 0x{offset:x} lies outside the verified track stride.");
        return (trackStruct, offset);
    }

    /// <summary>Read a mixer track's disk-recording arm state (the byte FL's own isTrackArmed reads).</summary>
    public async Task<bool> GetMixerTrackArmedAsync(int track, CancellationToken ct = default)
    {
        var (trackStruct, offset) = await ArmedFieldAsync(track, ct);
        return (await PeekAbsAsync(trackStruct + offset, 1, ct))[0] != 0;
    }

    /// <summary>Arm/disarm a mixer track for disk recording through FL's setter, exactly as FL's own
    /// armTrack callback does: no call when the state already matches, and the byte is re-read afterwards
    /// so a refused arm (for example a cancelled file dialog) is reported instead of assumed.</summary>
    public async Task SetMixerTrackArmedAsync(int track, bool armed, CancellationToken ct = default)
    {
        LogOp("SetMixerTrackArmed", $"track={track} armed={armed}");
        var (trackStruct, offset) = await ArmedFieldAsync(track, ct);
        if (((await PeekAbsAsync(trackStruct + offset, 1, ct))[0] != 0) == armed) return;
        await CallAsync(SetTrackArmedSymbol, new ulong[] { trackStruct, armed ? 1UL : 0UL, 0 }, ct);
        if (((await PeekAbsAsync(trackStruct + offset, 1, ct))[0] != 0) != armed)
            throw new InvalidOperationException(
                $"Mixer track {track} arm state did not change to {armed}; FL's setter returned without arming (a recording-name dialog may have been cancelled). Inspect the mixer before retrying.");
    }
}
