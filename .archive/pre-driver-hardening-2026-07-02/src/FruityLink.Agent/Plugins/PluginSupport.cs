namespace FruityLink.Agent.Plugins;

/// <summary>Shared helpers for the FL control plugins.</summary>
internal static class PluginSupport
{
    /// <summary>
    /// Message returned to the model when a tool fails, so the agent relays it instead of the
    /// function-calling loop throwing. Distinguishes a real bridge outage (pipe unreachable —
    /// FL not running / bridge not injected) from a logic/validation error (bad arguments, no
    /// project open, a native fault): the latter is surfaced verbatim so the agent can self-correct
    /// rather than wrongly concluding "the bridge is down".
    /// </summary>
    public static string BridgeError(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            // A bounded timeout means FL Studio IS running but its main (UI) thread didn't service the
            // call in time — it is busy or showing a modal dialog. Distinguish this from the bridge
            // being unreachable, so the agent gives the user accurate, actionable guidance rather than
            // wrongly telling them FL isn't running.
            if (e is TimeoutException)
                return $"FL Studio didn't respond in time — it may be busy or showing a dialog ({e.Message}). " +
                       "Try again in a moment; if FL stays frozen, it will need to be restarted.";
            if (e is IOException or System.ComponentModel.Win32Exception)
                return $"FL Studio's native bridge isn't reachable ({e.Message}). Make sure FL Studio is running and the FruityLink bridge is loaded.";
        }
        return $"That FL Studio operation failed: {ex.Message}";
    }
}
