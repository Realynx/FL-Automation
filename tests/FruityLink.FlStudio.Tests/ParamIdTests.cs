using FruityLink.FlStudio.Inject;
using Shouldly;
using Xunit;

namespace FruityLink.FlStudio.Tests;

/// <summary>
/// Characterization tests pinning the WIRE-FACING param-id math in <see cref="FlInjectBridge"/>.
/// These ids are dispatched straight into FL's command bus (FL_DispatchCommand), so the derived
/// values are reverse-engineered constants that must never drift — the expected values below were
/// computed from the live-verified formulas and lock them in place.
/// </summary>
public class ParamIdTests
{
    // ---- dispatch protocol constants (re/08-command-bus.md) ----

    [Fact]
    public void DispatchFlags_AreTheLiveVerifiedValues()
    {
        FlInjectBridge.FlagWheelOrScript.ShouldBe(0x3DDu);
        FlInjectBridge.FlagSet.ShouldBe(0x11u);   // bit0 = SET
        FlInjectBridge.FlagGet.ShouldBe(0x02u);   // bit1 = GET
    }

    [Fact]
    public void GlobalCommandIds_AreTheLiveVerifiedValues()
    {
        FlInjectBridge.CmdMasterVolume.ShouldBe(0x40000000u);
        FlInjectBridge.CmdShuffle.ShouldBe(0x40000001u);
        FlInjectBridge.CmdMasterPitch.ShouldBe(0x40000002u);
        FlInjectBridge.CmdSetTempo.ShouldBe(0x40000005u);
    }

    [Fact]
    public void MixerControlOffsets_AreTheLiveVerifiedValues()
    {
        FlInjectBridge.MixerVolOffset.ShouldBe(0x70001FC0u);
        FlInjectBridge.MixerPanOffset.ShouldBe(0x70001FC1u);
        FlInjectBridge.MixerStereoSepOffset.ShouldBe(0x70001FC2u);
    }

    [Fact]
    public void ChannelParamIndexes_AreTheLiveVerifiedValues()
    {
        FlInjectBridge.ChanVol.ShouldBe(0u);
        FlInjectBridge.ChanPan.ShouldBe(1u);
        FlInjectBridge.ChanPitch.ShouldBe(4u);
        FlInjectBridge.ChanMute.ShouldBe(7u);
        FlInjectBridge.ChanFxRoute.ShouldBe(8u);
    }

    // ---- mixer track controls: base(track) = (track*0x40) << 16, control offset added ----

    [Theory]
    [InlineData(0, 0x70001FC0u, 0x70001FC0u)]     // master volume
    [InlineData(1, 0x70001FC0u, 0x70401FC0u)]     // insert 1 volume
    [InlineData(1, 0x70001FC1u, 0x70401FC1u)]     // insert 1 pan
    [InlineData(125, 0x70001FC1u, 0x8F401FC1u)]   // last insert pan (high track wraps into bit 31)
    public void MixerTrackParamId_MatchesTheCommandBusEncoding(int track, uint offset, uint expected)
        => FlInjectBridge.MixerTrackParamId(track, offset).ShouldBe(expected);

    // ---- mixer FX-slot plugin params: ((track*0x40 + slot) << 16) + paramIndex + 0x70008000 ----

    [Theory]
    [InlineData(0, 0, 0, 0x70008000u)]
    [InlineData(1, 2, 3, 0x70428003u)]
    [InlineData(5, 9, 100, 0x71498064u)]
    public void MixerFxParamId_MatchesTheCommandBusEncoding(int track, int slot, int paramIndex, uint expected)
        => FlInjectBridge.MixerFxParamId(track, slot, paramIndex).ShouldBe(expected);

    // ---- channel params: (channel << 16) + paramIndex (recTag == index for unreordered projects) ----

    [Theory]
    [InlineData(0, 0u, 0x00000000u)]   // channel 0 volume
    [InlineData(0, 1u, 0x00000001u)]   // channel 0 pan
    [InlineData(3, 4u, 0x00030004u)]   // channel 3 pitch
    [InlineData(7, 7u, 0x00070007u)]   // channel 7 mute
    [InlineData(2, 8u, 0x00020008u)]   // channel 2 FX route
    public void ChannelParamId_MatchesTheCommandBusEncoding(int channel, uint paramIndex, uint expected)
        => FlInjectBridge.ChannelParamId(channel, paramIndex).ShouldBe(expected);
}
