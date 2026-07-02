using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Core.Domain;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// <see cref="ToolSubsetSelector"/> is the prompt-token diet for the ~84-tool surface: a keyword
/// hit must advertise the matching domain's action tools plus the always-on core set, an
/// unrecognized prompt must FAIL OPEN (null = full surface), and a previous-turn "wasn't defined"
/// tool error must force the full surface so a wrong guess costs at most one round. The kernel is
/// built from the REAL plugins (fake-backed) so resolution runs against genuine tool names.
/// </summary>
public sealed class ToolSubsetSelectorTests
{
    /// <summary>Kernel with the sub-agent plugin surface registered (NativeControl + MusicTheory +
    /// Knowledge). Orchestration is left out on purpose — the selector must skip its unresolvable
    /// core name instead of failing, mirroring sub-agent kernels which never register it.</summary>
    private static Kernel BuildKernel()
    {
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(new NativeControlPlugin(new FakeNativeFlControl()), "NativeControl");
        kernel.Plugins.AddFromObject(new MusicTheoryPlugin(new NullAuditSink(), new DefaultSettingsStore()), "MusicTheory");
        kernel.Plugins.AddFromObject(new KnowledgePlugin(new EmptyRetriever()), "Knowledge");
        return kernel;
    }

    private static IReadOnlyList<string> NamesFor(Kernel kernel, string userMessage, ChatHistory? history = null) =>
        ToolSubsetSelector.SelectForTurn(kernel, userMessage, history ?? new ChatHistory())?
            .Select(f => f.Name).ToArray() ?? Array.Empty<string>();

    [Fact]
    public void MixerPrompt_AdvertisesMixerActionsButNoPlaylistActions()
    {
        Kernel kernel = BuildKernel();

        IReadOnlyList<KernelFunction>? subset =
            ToolSubsetSelector.SelectForTurn(kernel, "make the mixer louder and boost the reverb send", new ChatHistory());

        subset.ShouldNotBeNull();
        var names = subset!.Select(f => f.Name).ToArray();
        names.ShouldContain("native_set_mixer_volume");
        names.ShouldContain("native_set_mixer_send");
        names.ShouldContain("native_add_mixer_effect");
        // Playlist/arrangement ACTION tools stay off the request — that schema is the token cost
        // the subsetting exists to cut.
        names.ShouldNotContain("native_add_pattern_clip");
        names.ShouldNotContain("native_delete_arrangement");
        names.ShouldNotContain("native_add_note");
    }

    [Fact]
    public void MatchedPrompt_AlwaysCarriesTheCoreSet()
    {
        Kernel kernel = BuildKernel();

        var names = NamesFor(kernel, "add some reverb to the mixer");

        // Named core tools (the registered ones; run_parallel_tasks is absent here by design).
        names.ShouldContain("get_creative_soul");
        names.ShouldContain("list_scales");
        names.ShouldContain("build_chord_progression");
        names.ShouldContain("search_knowledge");
        // Every read/list/get native tool rides along so the model can map names -> indices for
        // ANY request without a re-advertise round.
        names.ShouldContain("native_list_channels");
        names.ShouldContain("native_get_song_state");
        names.ShouldContain("native_list_samples");
        names.ShouldContain("native_get_ppq");
    }

    [Fact]
    public void UnknownPrompt_FailsOpenToTheFullSurface()
    {
        Kernel kernel = BuildKernel();

        ToolSubsetSelector.SelectForTurn(kernel, "hello there, how are you doing?", new ChatHistory())
            .ShouldBeNull();
    }

    [Theory]
    // Both fragments of SK's undefined-function tool error must trigger the fallback.
    [InlineData("Error: Function NativeControl-native_foo wasn't defined.")]
    [InlineData("Error: the requested function could not be found.")]
    public void PreviousTurnUndefinedFunctionError_FallsBackToFullSurface(string toolError)
    {
        Kernel kernel = BuildKernel();
        var history = new ChatHistory();
        history.AddUserMessage("pan the mixer track left");
        history.AddAssistantMessage("Trying a tool.");
        history.Add(new FunctionResultContent(
            new FunctionCallContent("native_foo", "NativeControl", "call_1"), toolError).ToChatMessage());

        // The keyword ("mixer") matches, but the previous turn proves our subset guessed wrong —
        // the selector must fall back to the full surface rather than guess wrong twice.
        ToolSubsetSelector.SelectForTurn(kernel, "try the mixer change again", history).ShouldBeNull();
    }

    [Fact]
    public void RecentHistory_KeepsTheDomainInScopeForFollowUps()
    {
        Kernel kernel = BuildKernel();
        var history = new ChatHistory();
        history.AddUserMessage("turn down the reverb send on the mixer");
        history.AddAssistantMessage("OK: send 1->2=0.5");

        // "make it more subtle" carries no domain keyword on its own; the two content-bearing
        // history messages must keep the mixer group matched.
        var names = NamesFor(kernel, "make it more subtle please", history);

        names.ShouldContain("native_set_mixer_send");
    }

    [Fact]
    public void SameInput_YieldsStableDeterministicOrdering()
    {
        Kernel kernel = BuildKernel();
        var history = new ChatHistory();

        var first = NamesFor(kernel, "set the mixer volume", history);
        var second = NamesFor(kernel, "set the mixer volume", history);

        first.ShouldNotBeEmpty();
        second.ShouldBe(first);   // registration/declaration order — stable across turns
    }

    // ---------------- minimal plugin-dependency fakes (never invoked by the selector) ----------------

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

    private sealed class EmptyRetriever : IKnowledgeRetriever
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeHit>>(Array.Empty<KnowledgeHit>());
    }
}
