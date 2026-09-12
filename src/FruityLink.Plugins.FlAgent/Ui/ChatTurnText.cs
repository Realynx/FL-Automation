using System;

namespace FruityLink.Plugins.FlAgent.Ui;

/// <summary>
/// The turn-fallback texts shared by BOTH chat surfaces (the Avalonia presenter and the legacy WPF
/// window), so neither depends on the other for a string constant and the wording can never drift
/// between them. Only the texts that are identical across surfaces live here — the full error-message
/// rendering intentionally differs per surface (the Avalonia path adds HTTP-status headers).
/// </summary>
internal static class ChatTurnText
{
    /// <summary>Appended to a bubble whose stream died AFTER partial answer text arrived — the
    /// partial is kept (it is also recorded in the agent's history) and the user is told how to
    /// resume.</summary>
    public const string PartialKeptNotice =
        "\n\n⚠ Connection dropped mid-reply — partial answer kept. Say \"continue\" to pick up from here.";

    /// <summary>Fallback answer when the turn produced no text but DID call tools / stream thoughts.</summary>
    public const string DoneFallback = "Done.";

    /// <summary>Fallback answer when the turn produced no text and no tool/thought activity at all.</summary>
    public const string NoTextFallback =
        "(The model returned no text. If it never calls tools, try a tool-capable model.)";

    /// <summary>Marker shown when the user Stops an in-flight turn (button reads "Stop").</summary>
    public const string Cancelled = "(stopped)";

    /// <summary>The exception detail both surfaces show: the message, plus the inner exception's
    /// message when it adds information.</summary>
    public static string BuildDetail(Exception ex)
    {
        string detail = ex.Message;
        if (ex.InnerException is not null && ex.InnerException.Message != ex.Message)
            detail += " — " + ex.InnerException.Message;
        return detail;
    }
}
