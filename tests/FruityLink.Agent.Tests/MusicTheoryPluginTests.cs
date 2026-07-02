using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Core.Domain;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// build_chord_progression emits its notes CSV at the composer's default PPQ (96 — FL's factory
/// timebase), but native_add_notes writes RAW ticks at the PROJECT's timebase, which is
/// user-configurable (often 960). The tool is deliberately offline (no bridge dependency), so it
/// cannot read the live PPQ itself — the result text must therefore state the tick base and give
/// the model an ACTIONABLE rescale rule, and must NOT instruct it to forward the ticks verbatim
/// (which would land ~10x-compressed notes on non-default-timebase projects).
/// </summary>
public sealed class MusicTheoryPluginTests
{
    private static MusicTheoryPlugin Plugin() => new(new NullAuditSink(), new DefaultSettingsStore());

    [Fact]
    public void BuildChordProgression_StatesTheTickBase_AndHowToRescale()
    {
        string result = Plugin().BuildChordProgression("C", "major", "1,6,4,5");

        result.ShouldStartWith("OK:");
        result.ShouldContain("PPQ=96");          // the tick base the CSV was computed at
        result.ShouldContain("native_get_ppq");  // where to read the project's actual timebase
        result.ShouldContain("multiply");        // the rescale rule, actionably
        result.ShouldNotContain("verbatim");     // the old anti-rescale instruction must be gone
    }

    [Fact]
    public void BuildChordProgression_StillEmitsTheNativeAddNotesCsvGrammar()
    {
        string result = Plugin().BuildChordProgression("C", "major", "1", beatsPerChord: 1);

        // One triad = three 'key,start,length,velocity' entries joined by "; " — C4/E4/G4 for one
        // beat at PPQ 96. The grammar itself must survive the wording fix.
        result.ShouldContain("60,0,96,");
        result.ShouldContain("64,0,96,");
        result.ShouldContain("67,0,96,");
    }

    private sealed class NullAuditSink : IOperationAuditSink
    {
        public void Record(FlOperation operation) { }
        public IReadOnlyList<FlOperation> Drain() => Array.Empty<FlOperation>();
    }

    private sealed class DefaultSettingsStore : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }
}
