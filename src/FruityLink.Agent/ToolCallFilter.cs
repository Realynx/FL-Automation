using System.IO;
using FruityLink.Llm.Diagnostics;
using Microsoft.SemanticKernel;

namespace FruityLink.Agent;

/// <summary>
/// Function-invocation filter with three jobs:
/// 1) SAFETY NET — any exception a tool lets escape becomes an "ERR: …" STRING result instead of
///    aborting the whole tool-calling turn, and oversized string results are truncated so a single
///    unfiltered list can't flood a small model's context. (NativeControl tools already return ERR
///    strings for bridge faults via <c>PluginSupport.Run</c>; this catch is the net for OTHER
///    plugins and unexpected exceptions.)
/// 2) OBSERVATION — raises <see cref="Invoked"/> after each tool so the UI can show the agent's
///    tool calls. Raised on the SK execution thread — subscribers must marshal to the UI thread.
/// 3) TRANSCRIPT — appends every call (name, args, result preview) to a per-day tools log so a
///    session's tool CHOICES are reviewable after the fact (the LLM diagnostics log only records
///    HTTP timings, not which tools ran) — the data needed to tune the tool surface.
/// </summary>
public sealed class ToolCallFilter : IFunctionInvocationFilter
{
    /// <summary>Cap on successful string results. 6KB ≈ 1.5-2k tokens — enough for any legitimate
    /// list page, small enough that one runaway result can't dominate a weak model's context.</summary>
    private const int MaxResultChars = 6 * 1024;

    /// <summary>Raised after each tool (plugin function) the agent invokes.</summary>
    public event Action<ToolCallInfo>? Invoked;

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        string? outcome = null;   // result/err preview for the transcript (null = cancelled before a result)
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await next(context).ConfigureAwait(false);

            // Cap oversized SUCCESSFUL string results. The suffix tells the model how to get the
            // rest (narrow the query / page with offset) instead of leaving it guessing why the
            // list just stops.
            if (context.Result.GetValue<object?>() is string s && s.Length > MaxResultChars)
                context.Result = new FunctionResult(context.Result, TruncateResult(s));
            outcome = context.Result.GetValue<object?>()?.ToString();
        }
        catch (OperationCanceledException)
        {
            // A cancelled turn is not a tool failure the model should try to "fix" — let it abort.
            throw;
        }
        catch (Exception ex)
        {
            // Convert the crash into a result the model can react to. Without this, one throwing
            // tool aborts the whole turn and the user sees a raw exception instead of a recovery.
            // The "DIFFERENT call" nudge matters: weak backends otherwise retry the identical
            // failing call in a loop until the iteration cap kills the turn.
            outcome = $"ERR: {context.Function.Name}: {FirstLine(ex.Message)}. " +
                "Fix arguments and try a DIFFERENT call — do not repeat the same call.";
            context.Result = new FunctionResult(context.Result, outcome);
        }
        finally
        {
            stopwatch.Stop();
            string args = SummarizeArguments(context.Arguments);
            // Append to the per-day transcript FIRST (best-effort, never throws) — the reviewable record
            // of what the model actually called with which arguments and how it went.
            WriteTranscript(context.Function.Name, args, outcome);
            // Also feed the STRUCTURED per-turn transcript (turns-*.jsonl): fuller args + result + timing,
            // correlated to the active turn via the ambient SessionTranscript scope. Best-effort.
            SessionTranscript.Current?.RecordTool(
                context.Function.Name, SummarizeArgumentsFull(context.Arguments), outcome, stopwatch.ElapsedMilliseconds);
            // Fire in finally so a tool that THROWS still shows in the UI (otherwise a failing tool call
            // is invisible and the flaky behavior is undiagnosable). Never let UI notification itself
            // break the function-calling loop.
            try
            {
                Invoked?.Invoke(new ToolCallInfo(context.Function.Name, args));
            }
            catch
            {
                // swallow — UI notification must not affect tool execution
            }
        }
    }

    /// <summary>Appends one <c>HH:mm:ss.fff name(args) -&gt; result</c> line to
    /// <c>%APPDATA%\FLAutomate\logs\tools-YYYYMMDD.log</c> so a session's tool choices can be reviewed
    /// afterward. Best-effort: any failure is swallowed so logging can never affect tool execution.</summary>
    // Don't pollute the user's real transcript when the filter runs under a test host (xUnit exercises
    // this filter with fake tools like "boom"/"echo"); those were showing up in tools-YYYYMMDD.log.
    private static readonly bool SuppressTranscript =
        (AppContext.GetData("FLAUTOMATE_TEST") as bool? ?? false)
        || (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty)
            .Contains("testhost", StringComparison.OrdinalIgnoreCase)
        || (AppDomain.CurrentDomain.FriendlyName ?? string.Empty)
            .Contains("testhost", StringComparison.OrdinalIgnoreCase);

    private static void WriteTranscript(string name, string args, string? result)
    {
        if (SuppressTranscript) return;
        try
        {
            string preview = (result ?? "(cancelled)").Replace('\r', ' ').Replace('\n', ' ');
            if (preview.Length > 400) preview = preview[..400] + "…";
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLAutomate", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, $"tools-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss.fff}  {name}({args}) -> {preview}{Environment.NewLine}");
        }
        catch { /* transcript logging must never break tool execution */ }
    }

    /// <summary>
    /// Truncates an oversized result at the last LINE boundary inside the budget — a list entry
    /// sliced in half reads like corrupted data and its dangling values can mislead the model —
    /// with an accurate "showing X of Y lines" count so the model knows how much is missing and
    /// that paging (offset) / filtering gets the rest. A payload with no usable line break in the
    /// budget (one giant line) falls back to a hard character cut.
    /// </summary>
    private static string TruncateResult(string s)
    {
        int cut = s.LastIndexOf('\n', MaxResultChars);
        if (cut < MaxResultChars / 2)
            return s[..MaxResultChars] + "… [truncated — use a filter or narrower query]";

        int totalLines = CountLines(s.AsSpan());
        int keptLines = CountLines(s.AsSpan(0, cut));
        return s[..cut].TrimEnd('\r') +
            $"\n… [truncated: showing {keptLines} of {totalLines} lines — repeat with a filter, offset, or narrower query]";
    }

    /// <summary>Line count = 1 + newline count (results are TrimEnd'ed, so no trailing newline).</summary>
    private static int CountLines(ReadOnlySpan<char> text)
    {
        int lines = 1;
        foreach (char c in text)
            if (c == '\n') lines++;
        return lines;
    }

    /// <summary>First line of an exception message: multi-line native/stack detail is noise the
    /// model pays tokens for without gaining anything actionable.</summary>
    private static string FirstLine(string message)
    {
        int nl = message.IndexOfAny(new[] { '\r', '\n' });
        return nl < 0 ? message : message[..nl];
    }

    private static string SummarizeArguments(KernelArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0) return string.Empty;

        var parts = new List<string>(arguments.Count);
        foreach (KeyValuePair<string, object?> arg in arguments)
        {
            string value = arg.Value?.ToString() ?? string.Empty;
            if (value.Length > 64) value = value[..64] + "…";
            parts.Add($"{arg.Key}={value}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Fuller argument rendering for the STRUCTURED transcript — each value capped at 1000
    /// (the plaintext <see cref="SummarizeArguments"/> caps at 64, too short to review what the model
    /// actually passed, e.g. a note list). The JSONL writer caps the joined string again.</summary>
    private static string SummarizeArgumentsFull(KernelArguments? arguments)
    {
        if (arguments is null || arguments.Count == 0) return string.Empty;

        var parts = new List<string>(arguments.Count);
        foreach (KeyValuePair<string, object?> arg in arguments)
        {
            string value = arg.Value?.ToString() ?? string.Empty;
            if (value.Length > 1000) value = value[..1000] + "…";
            parts.Add($"{arg.Key}={value}");
        }
        return string.Join(", ", parts);
    }
}
