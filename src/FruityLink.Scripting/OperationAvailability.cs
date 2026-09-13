using FruityLink.Core.Abstractions;
using System.Text.Json;

namespace FruityLink.Scripting;

internal static class OperationAvailability
{
    private static readonly HashSet<string> RawMixer = new(StringComparer.Ordinal)
    {
        "get_mixer_track_muted", "set_mixer_track_muted", "get_mixer_track_name", "set_mixer_track_name",
        "list_mixer_tracks", "set_mixer_send", "list_mixer_effects", "add_mixer_effect", "remove_mixer_effect",
        "clone_mixer_effect", "add_mixer_track", "query_mixer_tracks"
    };
    private static readonly HashSet<string> Chat = new(StringComparer.Ordinal)
        { "open_chat_tab", "close_chat_tab", "chat_poll", "chat_say" };
    private static readonly HashSet<string> ConditionalMixer = new(StringComparer.Ordinal)
        { "list_plugin_params", "set_plugin_param", "query_plugin_parameters" };
    private static readonly HashSet<string> Timeline = new(StringComparer.Ordinal)
        { "add_marker", "list_markers", "set_loop_region" };
    private static readonly HashSet<string> ProjectSave = new(StringComparer.Ordinal)
        { "save_project", "save_project_as", "save_copy", "save_new_version" };
    private static readonly HashSet<string> Automation = new(StringComparer.Ordinal)
    {
        "create_automation_clip", "add_automation_clip", "set_automation_points", "add_automation_point",
        "delete_automation_point", "list_automation_points", "query_automation_points"
    };

    internal static IReadOnlyList<string> Requirements(string operation)
    {
        if (RawMixer.Contains(operation)) return new[] { "mixer_layout" };
        if (Chat.Contains(operation)) return new[] { "legacy_browser_ui" };
        if (ConditionalMixer.Contains(operation)) return new[] { "mixer_layout_for_effect_slot" };
        if (Timeline.Contains(operation)) return new[] { "timeline_layout" };
        if (Automation.Contains(operation)) return new[] { "automation_clips" };
        if (ProjectSave.Contains(operation)) return new[] { "project_note_validation" };
        return Array.Empty<string>();
    }

    internal static string? Unavailable(string operation, FlSymbolStatus? status, JsonElement? arguments = null)
    {
        if (operation == "is_available" || status is null) return null;
        if (status.Supported == false) return "The native scanner does not support this FL Studio version.";
        if (status.Complete == false) return "Native scanner initialization is still in progress.";
        if (SaveUnavailable(operation, status) is { } saveError) return saveError;
        if (Automation.Contains(operation)) return AutomationUnavailable(status);
        if (NeedsMixer(operation, arguments) && status.MixerLayout is null)
            return "This operation requires a complete verified mixer layout for the running FL Studio build.";
        if (operation == "add_mixer_track" && status.Unresolved.Contains("FLmx_InsertTracks"))
            return "The native mixer-track insertion function could not be resolved for the running FL Studio build.";
        if (Timeline.Contains(operation) && status.TimelineLayout is null)
            return "This operation requires a verified timeline layout for the running FL Studio build.";
        if (Chat.Contains(operation) && status.FileVersion != "25.2.5.5319")
            return "The legacy browser UI layout is verified only for FL Studio 25.2.5.5319.";
        return null;
    }

    private static string? SaveUnavailable(string operation, FlSymbolStatus status)
        => ProjectSave.Contains(operation) &&
            (status.Unresolved.Contains("NoteRecorderArrayBase") || status.Unresolved.Contains("ChannelList"))
            ? "Saving requires resolved note-recorder and channel-list symbols to check for invalid notes before invoking FL's serializer."
            : null;

    private static string? AutomationUnavailable(FlSymbolStatus status)
    {
        if (status.AutomationClips != true)
            return "Automation clips require a verified layout for this exact FL Studio build.";
        string[] required = { "FLac_CreateForEvent", "ChannelList", "DynArrayTypeInfo", "Delphi_DynArraySetLength" };
        return required.Any(status.Unresolved.Contains)
            ? "Required native automation functions could not be resolved for this FL Studio build." : null;
    }

    private static bool NeedsMixer(string operation, JsonElement? arguments)
    {
        if (RawMixer.Contains(operation)) return true;
        return ConditionalMixer.Contains(operation) && arguments is { } value &&
            value.TryGetProperty("slot", out var slot) && slot.TryGetInt32(out int index) && index >= 0;
    }
}
