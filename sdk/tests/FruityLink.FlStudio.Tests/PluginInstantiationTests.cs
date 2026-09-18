using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

/// <summary>The first plugin instantiation of a session blocks FL's UI thread while the plugin is
/// constructed. Live on FL 26.1.3.5570 a cold add_mixer_effect(track: 20, slot: 0, "Fruity Parametric EQ 2")
/// outran the old 5000 ms guard and an immediate retry then succeeded, so the guard was measuring first-load
/// latency and the error text blamed a licence dialog that never existed. These cover the 20 s budget and the
/// single post-timeout re-inspection that decides whether the load actually failed.</summary>
public sealed class PluginInstantiationTests
{
    private const string Plugin = "Fruity Parametric EQ 2";

    [Fact]
    public void GeneratorEffectAndPresetLoadsShareTheTwentySecondBudget()
    {
        Assert.Equal(20000, FlInjectBridge.PluginInstantiationTimeoutMs);
        Assert.InRange(FlInjectBridge.PluginInstantiationSettleMs, 250, 5000);
    }

    [Fact]
    public async Task AnInstantiationThatFinishesInsideTheBudgetAddsNoNoteAndNeverReinspects()
    {
        var inspected = 0;

        string note = await FlInjectBridge.LoadPluginWithRecoveryAsync("mixer track 20 slot 0", Plugin,
            () => Task.CompletedTask,
            () => { inspected++; return Task.FromResult(Plugin); },
            _ => throw new InvalidOperationException("a completed load must not settle"));

        Assert.Equal("", note);
        Assert.Equal(0, inspected);
    }

    [Fact]
    public async Task ATimeoutThatActuallyLoadedReturnsNormallyWithANoteAfterOneSettledReinspection()
    {
        var inspected = 0;
        var settles = new List<int>();

        string note = await FlInjectBridge.LoadPluginWithRecoveryAsync("mixer track 20 slot 0", Plugin,
            () => throw new TimeoutException("FL Studio did not respond within 20000 ms"),
            () => { inspected++; return Task.FromResult(Plugin); },
            delay => { settles.Add(delay); return Task.CompletedTask; });

        Assert.Equal(1, inspected);
        Assert.Equal([FlInjectBridge.PluginInstantiationSettleMs], settles);
        Assert.Matches(@"^ \(loaded after \d+ ms; FL's UI was blocked while the plugin initialised\)$", note);
    }

    [Fact]
    public async Task AnEmptyTargetStillFailsButNamesInitialisationInsteadOfALicencePrompt()
    {
        var inspected = 0;

        var error = await Assert.ThrowsAsync<TimeoutException>(() => FlInjectBridge.LoadPluginWithRecoveryAsync(
            "mixer track 20 slot 0", Plugin,
            () => throw new TimeoutException("FL Studio did not respond within 20000 ms"),
            () => { inspected++; return Task.FromResult(""); },
            _ => Task.CompletedTask));

        Assert.Equal(1, inspected);   // one re-inspection, never a retry loop
        Assert.Contains("within 20000 ms while instantiating 'Fruity Parametric EQ 2'", error.Message);
        Assert.Contains("mixer track 20 slot 0 is still empty", error.Message);
        Assert.Contains("the plugin is still initialising or waiting on a dialog", error.Message);
        Assert.DoesNotContain("licence", error.Message);
        Assert.DoesNotContain("sign-in", error.Message);
        Assert.IsType<TimeoutException>(error.InnerException);
    }

    [Fact]
    public async Task AReinspectionFlRefusesToAnswerIsReportedAsSuchRatherThanAsAnEmptySlot()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() => FlInjectBridge.LoadPluginWithRecoveryAsync(
            "channel 7", Plugin,
            () => throw new TimeoutException("FL Studio did not respond within 20000 ms"),
            () => throw new TimeoutException("FL Studio did not respond within 4000 ms"),
            _ => Task.CompletedTask));

        Assert.Contains("FL did not answer the follow-up inspection of channel 7", error.Message);
        Assert.Contains("the plugin is still initialising or waiting on a dialog", error.Message);
        Assert.DoesNotContain("is still empty", error.Message);
    }

    [Fact]
    public async Task ANonTimeoutInstantiationFailureIsNotRecoveredFrom()
    {
        var inspected = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => FlInjectBridge.LoadPluginWithRecoveryAsync(
            "channel 7", Plugin,
            () => throw new InvalidOperationException("callabs faulted (ok:0)"),
            () => { inspected++; return Task.FromResult(Plugin); },
            _ => Task.CompletedTask));

        Assert.Equal(0, inspected);
    }

    [Fact]
    public void TheMixerEffectLoaderAdvertisesItsVerificationLineAndBudgetInTheContract()
    {
        var method = typeof(INativeFlControl).GetMethod(nameof(INativeFlControl.AddMixerEffectAsync))!;
        Assert.Equal(typeof(Task<string>), method.ReturnType);
        Assert.Equal(typeof(Task<int>), typeof(INativeFlControl).GetMethod(nameof(INativeFlControl.AddChannelAsync))!.ReturnType);
    }
}
