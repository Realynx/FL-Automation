namespace FruityLink.Agent.Plugins;

/// <summary>
/// Shared helpers for the FL control plugins. Centralizes the tool-result envelope ("OK: " /
/// "ERR: ") plus guard/clamp reporting so every tool speaks one compact, machine-scannable dialect:
/// weak backends learn the pattern once and can parse every result the same way, and success prose
/// stays terse (output tokens are the product's cost line).
/// </summary>
internal static class PluginSupport
{
    /// <summary>Success envelope. Keep the payload terse ("mixer 3 vol=6400") — no prose padding.</summary>
    public static string Ok(string msg) => "OK: " + msg;

    /// <summary>Failure envelope. The payload should tell the model how to self-correct.</summary>
    public static string Err(string msg) => "ERR: " + msg;

    /// <summary>
    /// Range guard for index-like parameters where clamping would silently target the WRONG object
    /// (a hallucinated mixer/playlist track must fail loudly, not act on a neighbor). Returns an
    /// ERR string with the valid range spelled out, or null when the value is valid.
    /// <paramref name="hi"/> == <see cref="int.MaxValue"/> renders as "must be &gt;= lo" for
    /// open-ended counts (channel indices) where printing 2147483647 would only confuse the model.
    /// </summary>
    public static string? Guard(string paramName, int value, int lo, int hi)
        => value >= lo && value <= hi
            ? null
            : Err(hi == int.MaxValue
                ? $"{paramName} {value} invalid — must be >= {lo}"
                : $"{paramName} {value} out of range ({lo}-{hi})");

    /// <summary>
    /// Applied-value report for magnitude parameters (volume/pan/level) where clamping IS the right
    /// behavior: state what was applied, and mention the clamp ONLY when it changed the value, so
    /// the model can recalibrate its scale without the happy path burning extra tokens.
    /// </summary>
    public static string ClampReport(string what, long requested, long clamped, long lo, long hi)
        => requested == clamped
            ? Ok($"{what}={clamped}")
            : Ok($"{what}={clamped} (requested {requested}; valid {lo}-{hi})");

    /// <summary>Floating-point overload of <see cref="ClampReport(string, long, long, long, long)"/>.</summary>
    public static string ClampReport(string what, double requested, double clamped, double lo, double hi)
        => requested == clamped
            ? Ok($"{what}={clamped:0.###}")
            : Ok($"{what}={clamped:0.###} (requested {requested:0.###}; valid {lo:0.###}-{hi:0.###})");

    /// <summary>
    /// Central try/catch for tool bodies: run the op and turn ANY failure into an ERR-enveloped
    /// <see cref="BridgeError"/> message — the SK loop must always receive a string the model can
    /// react to, never an exception. Cancellation still propagates: an aborted turn is not a tool
    /// failure the model should try to "fix".
    /// </summary>
    public static async Task<string> Run(Func<Task<string>> op)
    {
        try { return await op().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Err(BridgeError(ex)); }
    }

    /// <summary>Lenient integer parse: accepts plain ints and decimals (rounded), since weak models
    /// often emit '480.0' where a whole number is expected. Shared by the note-list and chord-degree
    /// parsers so both surfaces have ONE tolerance rule.</summary>
    public static bool TryNum(string s, out int v)
    {
        if (int.TryParse(s, out v)) return true;
        if (double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
        {
            v = (int)Math.Round(d);
            return true;
        }
        v = 0;
        return false;
    }

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
