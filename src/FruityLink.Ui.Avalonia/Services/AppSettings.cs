namespace FruityLink.Ui.Avalonia.Services;

/// <summary>
/// The persisted user settings for the interior UI — a plain POCO serialized to
/// <c>%APPDATA%\FruityLink\ui-settings.json</c> by <see cref="SettingsService"/>.
///
/// <para><b>Adding a setting is a one-property change here</b> (plus one row in the Settings view and,
/// if the chat reacts to it live, a matching property on the ChatViewModel). Give every property a
/// sensible default so an older/missing JSON file — or a freshly installed machine — still loads cleanly.</para>
/// </summary>
public sealed class AppSettings
{
    // ---- Debug / diagnostics --------------------------------------------------

    /// <summary>Show the streamed "thinking" block on assistant turns.</summary>
    public bool ShowThoughts { get; set; } = true;

    /// <summary>Show the tool-call chips on assistant turns.</summary>
    public bool ShowToolCalls { get; set; } = true;

    // ---- Chat behaviour -------------------------------------------------------

    /// <summary>Keep the transcript pinned to the newest message as the conversation grows.</summary>
    public bool AutoScroll { get; set; } = true;
}
