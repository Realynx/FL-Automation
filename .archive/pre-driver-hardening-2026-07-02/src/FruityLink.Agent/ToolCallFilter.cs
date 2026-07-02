using Microsoft.SemanticKernel;

namespace FruityLink.Agent;

/// <summary>
/// Observes auto-invoked kernel functions so the UI can show the agent's tool calls.
/// Raised on the SK execution thread — subscribers must marshal to the UI thread.
/// </summary>
public sealed class ToolCallFilter : IFunctionInvocationFilter
{
    /// <summary>Raised after each tool (plugin function) the agent invokes.</summary>
    public event Action<ToolCallInfo>? Invoked;

    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // Fire in finally so a tool that THROWS still shows in the UI (otherwise a failing tool call
            // is invisible and the flaky behavior is undiagnosable). Never let UI notification itself
            // break the function-calling loop.
            try
            {
                Invoked?.Invoke(new ToolCallInfo(context.Function.Name, SummarizeArguments(context.Arguments)));
            }
            catch
            {
                // swallow — UI notification must not affect tool execution
            }
        }
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
}
