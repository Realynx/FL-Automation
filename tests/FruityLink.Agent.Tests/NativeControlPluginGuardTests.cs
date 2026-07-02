using FruityLink.Agent.Plugins;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// The <see cref="NativeControlPlugin"/> guards are the last line between a hallucinated index and
/// a raw write into FL's process memory (mixer sends are poked straight into the routing matrix).
/// An out-of-range index must return "ERR: …" WITHOUT the call ever reaching the bridge — the
/// recording <see cref="FakeNativeFlControl"/> proves it — while magnitude values clamp, report,
/// and go through. Table-driven over the guard matrix so new guards join one list, not new tests.
/// </summary>
public sealed class NativeControlPluginGuardTests
{
    private readonly FakeNativeFlControl _fl = new();
    private readonly NativeControlPlugin _plugin;

    public NativeControlPluginGuardTests() => _plugin = new NativeControlPlugin(_fl);

    /// <summary>The guard matrix: calls whose arguments must be REJECTED before the bridge.</summary>
    public static TheoryData<string, Func<NativeControlPlugin, Task<string>>> RejectedCalls() => new()
    {
        // Mixer send: BOTH endpoints are guarded (0-125) — an out-of-range slot address would
        // write raw FL memory at the wrong place.
        { "send src 126", p => p.SetMixerSendAsync(126, 0, 0.5) },
        { "send src -1", p => p.SetMixerSendAsync(-1, 0, 0.5) },
        { "send dst 126", p => p.SetMixerSendAsync(0, 126, 0.5) },
        { "send dst -1", p => p.SetMixerSendAsync(0, -1, 0.5) },
        // Playlist tracks are 1-based 1-500: 0 and 501 are the classic off-by-one hallucinations.
        { "track name 0", p => p.SetTrackNameAsync(0, "Drums") },
        { "track name 501", p => p.SetTrackNameAsync(501, "Drums") },
        { "track mute 0", p => p.SetTrackMuteAsync(0, true) },
        { "track select 501", p => p.SelectTrackAsync(501) },
        { "pattern clip track 0", p => p.AddPatternClipsAsync("[{\"pattern\":1,\"track\":0,\"start\":0,\"length\":0}]") },
        // Mixer EQ: track and band are index guards (the dB VALUE clamps instead — see below).
        { "eq track 126", p => p.SetMixerEqGainAsync(126, 0, 0.0) },
        { "eq band 3", p => p.SetMixerEqGainAsync(0, 3, 0.0) },
        { "eq band -1", p => p.SetMixerEqGainAsync(0, -1, 0.0) },
        // Mixer track / FX slot / channel index guards.
        { "mixer volume track -1", p => p.SetMixerVolumeAsync(-1, 6400) },
        { "mixer volume track 126", p => p.SetMixerVolumeAsync(126, 6400) },
        { "fx slot 10", p => p.AddMixerEffectAsync(0, 10, "Fruity Reeverb 2") },
        { "channel -1", p => p.SetChannelVolumeAsync(-1, 10000) },
        { "route to mixer 126", p => p.SetChannelFxRouteAsync(0, 126) },
        // Color pre-validation: junk hex would otherwise become a wrong color or a FormatException.
        { "color 4-digit hex", p => p.SetTrackColorAsync(1, "FF88") },
        { "color non-hex", p => p.SetTrackColorAsync(1, "GGHHII") },
        { "color empty", p => p.SetTrackColorAsync(1, "") },
    };

    /// <summary>In-range counterparts: the same tools MUST reach the bridge when arguments are valid.</summary>
    public static TheoryData<string, Func<NativeControlPlugin, Task<string>>, string> AcceptedCalls() => new()
    {
        { "send", p => p.SetMixerSendAsync(1, 2, 1.0), "SetMixerSendAsync(1,2,1)" },
        { "track name", p => p.SetTrackNameAsync(1, "Drums"), "SetTrackNameAsync(1,Drums)" },
        { "track 500 (upper bound)", p => p.SelectTrackAsync(500), "SelectTrackAsync(500)" },
        { "mixer volume track 0 (master)", p => p.SetMixerVolumeAsync(0, 6400), "SetMixerVolumeAsync(0,6400)" },
        { "mixer volume track 125 (upper bound)", p => p.SetMixerVolumeAsync(125, 6400), "SetMixerVolumeAsync(125,6400)" },
        { "fx slot 9 (upper bound)", p => p.AddMixerEffectAsync(0, 9, "Fruity Limiter"), "AddMixerEffectAsync(0,9,Fruity Limiter)" },
        // #FF8800 -> RGB int 16746496; '#' and case are tolerated.
        { "color hex", p => p.SetTrackColorAsync(2, "#FF8800"), "SetTrackColorAsync(2,16746496)" },
        // 0 dB maps to FL's fixed-point midpoint 0x20000000.
        { "eq 0 dB midpoint", p => p.SetMixerEqGainAsync(3, 1, 0.0), "SetMixerEqGainAsync(3,1,536870912)" },
    };

    [Theory]
    [MemberData(nameof(RejectedCalls))]
    public async Task OutOfRangeArgument_ReturnsErrWithoutTouchingTheBridge(
        string @case, Func<NativeControlPlugin, Task<string>> call)
    {
        string result = await call(_plugin);

        result.ShouldStartWith("ERR:", customMessage: @case);
        _fl.Calls.ShouldBeEmpty(@case);
    }

    [Theory]
    [MemberData(nameof(AcceptedCalls))]
    public async Task InRangeArgument_ReachesTheBridge(
        string @case, Func<NativeControlPlugin, Task<string>> call, string expectedBridgeCall)
    {
        string result = await call(_plugin);

        result.ShouldStartWith("OK:", customMessage: @case);
        _fl.Calls.ShouldBe(new[] { expectedBridgeCall }, ignoreOrder: false, customMessage: @case);
    }

    // ---------------- clamp semantics (magnitudes clamp + report; they still go through) ----------------

    [Fact]
    public async Task ClampedMagnitude_ReportsRequestedAndValidRange()
    {
        string result = await _plugin.SetTempoAsync(1000);

        // ClampReport wording: applied value first, then the recalibration hint.
        result.ShouldBe("OK: tempo=522 (requested 1000; valid 10-522)");
        _fl.Calls.ShouldBe(new[] { "SetTempoAsync(522)" });
    }

    [Fact]
    public async Task UnclampedMagnitude_ReportsValueOnlyWithNoClampNoise()
    {
        string result = await _plugin.SetTempoAsync(140);

        // The happy path must not burn tokens mentioning a clamp that didn't happen.
        result.ShouldBe("OK: tempo=140");
    }

    [Fact]
    public async Task EqGainDb_ClampsToPlusMinus18AndStillReachesTheBridge()
    {
        string result = await _plugin.SetMixerEqGainAsync(0, 0, 40.0);

        // dB is a magnitude, not an index: out-of-range CLAMPS (unlike track/band, which reject).
        result.ShouldBe("OK: mixer 0 EQ band 0 dB=18 (requested 40; valid -18-18)");
        // +18 dB = full-scale fixed point (2^30).
        _fl.Calls.ShouldBe(new[] { "SetMixerEqGainAsync(0,0,1073741824)" });
    }

    [Fact]
    public async Task IndexGuardError_SpellsOutTheValidRange()
    {
        string result = await _plugin.SetMixerVolumeAsync(126, 6400);

        result.ShouldBe("ERR: mixer track 126 out of range (0-125)");
    }

    [Fact]
    public async Task OpenEndedIndexGuardError_UsesTheGreaterEqualWording()
    {
        // Open-ended counts (channel indices) must not print int.MaxValue at the model.
        string result = await _plugin.SetChannelVolumeAsync(-2, 10000);

        result.ShouldBe("ERR: channel -2 invalid — must be >= 0");
    }
}
