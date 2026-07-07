using FruityLink.Core.Abstractions;
using FruityLink.Llm;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Agent;

/// <summary>
/// Drives the in-FL chat tab: a background loop polls the injected bridge for messages the user
/// submits in FL's "FruityLink AI" browser tab, runs them through a dedicated conversational agent
/// (its own history; same glm backend + full FL tool set as the main agent, so it actually automates
/// FL), and pushes the reply back into the tab's display. Comms reuse the existing pipe
/// (chattab_open / chat_poll / chat_say). Separate conversation from the WPF chat by design.
/// A thin adapter over <see cref="AgentKernelBuilder"/> + <see cref="AgentTurnRunner"/> — no turn
/// logic of its own.
/// </summary>
public sealed class ChatBridgeService(
    IChatKernelFactory kernelFactory,
    ISettingsStore settingsStore,
    FlPluginSet plugins,
    ToolCallFilter toolFilter,
    INativeFlControl fl,
    AgentPromptOptions? promptOptions = null)
{
    private readonly ChatHistory _history = new();
    private readonly AgentKernelBuilder _builder = new(kernelFactory, settingsStore);
    private readonly AgentTurnRunner _runner = new();
    private readonly AgentPromptOptions _promptOptions = promptOptions ?? new();

    /// <summary>Guards <see cref="_agentKernel"/>/<see cref="_kernelGeneration"/>: the poll loop
    /// reads them per turn while <see cref="InvalidateKernel"/> may reset them from a settings-save
    /// thread.</summary>
    private readonly object _kernelLock = new();
    private AgentKernel? _agentKernel;

    /// <summary>Bumped by <see cref="InvalidateKernel"/> so a build that STARTED before an
    /// invalidation (and so loaded the pre-save settings) is never cached as current.</summary>
    private int _kernelGeneration;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Diagnostic logger for the poll loop. Opt-in (FRUITYLINK_CHATLOOP_LOG=1) because an
    // always-on AppendAllText in %TEMP% grows without bound across long FL sessions; when enabled
    // it is truncated once it exceeds 1 MB — recent entries are the only ones that matter when
    // debugging "the tab went quiet".
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "fruitylink-chatloop.log");
    private static readonly bool LogEnabled =
        Environment.GetEnvironmentVariable("FRUITYLINK_CHATLOOP_LOG") is "1" or "true";
    private const long MaxLogBytes = 1024 * 1024;

    private static void Log(string m)
    {
        if (!LogEnabled) return;
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes)
                File.WriteAllText(LogPath, string.Empty);
            File.AppendAllText(LogPath, m + Environment.NewLine);
        }
        catch { /* diagnostic only */ }
    }

    /// <summary>Start the background poll loop (idempotent — concurrent calls start exactly one
    /// loop). Safe to call before FL/the bridge is up.</summary>
    public void Start()
    {
        var cts = new CancellationTokenSource();
        // Cold task so the loop only runs if THIS call wins the publish race; Unwrap so _loop
        // completes when the loop itself does (StopAsync awaits it).
        var pending = new Task<Task>(() => LoopAsync(cts.Token));
        if (Interlocked.CompareExchange(ref _loop, pending.Unwrap(), null) is not null)
        {
            cts.Dispose();   // lost the race — another Start already owns the loop
            return;
        }
        _cts = cts;
        Log("Start() invoked");
        pending.Start(TaskScheduler.Default);
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch { /* shutdown */ }
        _cts = null;
        _loop = null;
    }

    /// <summary>
    /// Drops the cached kernel so the NEXT turn rebuilds it from the settings on disk. Called after
    /// a backend save (see <c>AgentBackendSettingsGateway</c>) so a backend switch reaches the in-FL
    /// tab too, not just the main agent. The conversation history is kept — only the connection is
    /// remade.
    /// </summary>
    public void InvalidateKernel()
    {
        lock (_kernelLock)
        {
            _agentKernel = null;
            _kernelGeneration++;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        Log("LoopAsync entered");
        bool tabOpened = false;
        bool? lastAvail = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool avail = await fl.IsAvailableAsync(ct).ConfigureAwait(false);
                if (avail != lastAvail) { Log($"available={avail}"); lastAvail = avail; }
                if (!avail)
                {
                    tabOpened = false;                 // bridge gone; re-open when it returns
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }
                if (!tabOpened)
                {
                    Log("opening chat tab");
                    await fl.OpenChatTabAsync(ct).ConfigureAwait(false);
                    // Visible proof the loop is alive + polling (and a hint for the user).
                    await fl.ChatSayAsync("FruityLink AI ready — type a request and click Send.", ct).ConfigureAwait(false);
                    Log("chat tab opened + marker sent");
                    tabOpened = true;
                }

                string input = await fl.ChatPollAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(input))
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    continue;
                }

                await fl.ChatSayAsync("you: " + input, ct).ConfigureAwait(false);
                string reply = await RunTurnAsync(input, ct).ConfigureAwait(false);
                await fl.ChatSayAsync(reply, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                try { await fl.ChatSayAsync("[error] " + ex.Message, CancellationToken.None).ConfigureAwait(false); } catch { /* best effort */ }
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task<string> RunTurnAsync(string input, CancellationToken ct)
    {
        AgentKernel agentKernel = await GetOrBuildKernelAsync(ct).ConfigureAwait(false);

        // The runner rolls a failed turn's half-applied tool edits back (keeping the user message +
        // the prior conversation), so no orphaned tool_calls survive. That matters doubly here: this
        // loop swallows exceptions and continues, so a poisoned history would otherwise wedge the tab
        // for the rest of the FL session.
        TurnResult result = await _runner
            .RunTurnAsync(agentKernel.Kernel, agentKernel.Chat, _history, input, agentKernel.Settings, ct: ct)
            .ConfigureAwait(false);

        // The in-FL tab shows plain text only (no thoughts pane): the runner already stripped the
        // <think> reasoning; a tool-only turn with no prose still deserves a visible reply.
        return string.IsNullOrWhiteSpace(result.Text) ? "(done)" : result.Text;
    }

    private async Task<AgentKernel> GetOrBuildKernelAsync(CancellationToken ct)
    {
        int generation;
        lock (_kernelLock)
        {
            if (_agentKernel is not null)
                return _agentKernel;
            generation = _kernelGeneration;
        }

        AgentKernel built = await _builder.BuildAsync(plugins.All, toolFilter, ct: ct).ConfigureAwait(false);
        if (_history.Count == 0)
            _history.AddSystemMessage(SystemPrompts.BuildDefault(_promptOptions));

        lock (_kernelLock)
        {
            // Cache only if no InvalidateKernel landed while we were building — a build that
            // started before the invalidation read the PRE-save settings and must not stick.
            // Either way the current turn runs on what we just built (worst case one turn on the
            // old backend; the next turn rebuilds fresh).
            if (generation == _kernelGeneration)
                _agentKernel = built;
        }
        return built;
    }
}
