using System.Linq;
using FruityLink.Core.Abstractions;
using System.Text.Json;

namespace FruityLink.Scripting;

internal static class OperationAvailability
{
    private static readonly HashSet<string> RawMixer = new(StringComparer.Ordinal)
    {
        "get_mixer_track_muted", "set_mixer_track_muted", "get_mixer_track_name", "set_mixer_track_name",
        "list_mixer_tracks", "set_mixer_send", "list_mixer_effects", "add_mixer_effect", "remove_mixer_effect",
        "clone_mixer_effect", "add_mixer_track", "query_mixer_tracks", "query_mixer_sends", "load_mixer_effect_state", "get_mixer_effect_state"
    };
    // State reads snapshot the project through FL's serializer, so they share the save-time note validation.
    private static readonly HashSet<string> StateSnapshot = new(StringComparer.Ordinal)
        { "get_channel_plugin_state", "get_mixer_effect_state" };
    private static readonly HashSet<string> Chat = new(StringComparer.Ordinal)
        { "open_chat_tab", "close_chat_tab", "chat_poll", "chat_say" };
    private static readonly HashSet<string> ConditionalMixer = new(StringComparer.Ordinal)
        { "list_plugin_params", "set_plugin_param", "query_plugin_parameters" };
    private static readonly HashSet<string> Timeline = new(StringComparer.Ordinal)
        { "add_marker", "delete_marker", "list_markers", "set_loop_region" };
    // Disk-recording arm: the setter thunk and the armed-byte offset decode from one signature over FL's own
    // armTrack callback; the raw-mixer layout is needed for the track struct as well.
    private static readonly HashSet<string> MixerArm = new(StringComparer.Ordinal)
        { "set_mixer_track_armed", "get_mixer_track_armed" };
    private static readonly string[] MixerArmSymbols = { "FLmx_SetTrackArmed", "MixerTrackArmedOffset" };
    // Recording filter: one signature over FL's own "Recording filter" menu handler yields both the pointer
    // to the live bitmask and FL's setter, so the two symbols are refused together on an unmatched build.
    private static readonly HashSet<string> RecordingFilter = new(StringComparer.Ordinal)
        { "get_recording_filter", "set_recording_filter" };
    private static readonly string[] RecordingFilterSymbols = { "RecordingFilterPtr", "FLrec_SetRecordingFilter" };
    // The record button's pressed state: form pointer plus the two field offsets decode from one signature
    // over FL's own ui.isRecording callback.
    // Transport toggles: each name maps to the two symbols its own Action signature yields, plus the shared
    // options manager slot/field the setters are reached through.
    private static readonly Dictionary<string, string[]> TransportToggleSymbols = new(StringComparer.Ordinal)
    {
        ["metronome"] = new[] { "MetronomeStatePtr", "MetronomeSetterSlot" },
        ["countdown"] = new[] { "PrecountStatePtr", "PrecountSetterSlot" },
        ["wait_for_input"] = new[] { "WaitForInputStatePtr", "WaitForInputSetterSlot" },
        ["loop_record"] = new[] { "LoopRecordStatePtr", "LoopRecordSetterSlot" },
        ["blend_recorded_notes"] = new[] { "BlendRecordedStatePtr", "BlendRecordedSetterSlot" },
    };
    private static readonly string[] TransportOptionsSymbols = { "TransportOptionsPtr", "TransportOptionsFieldOffset" };

    /// <summary>"get_metronome"/"set_loop_record"/... -> the toggle key, or null when it is not a toggle op.</summary>
    private static string? ToggleKey(string operation)
    {
        foreach (string prefix in new[] { "get_", "set_" })
            if (operation.StartsWith(prefix, StringComparison.Ordinal) &&
                TransportToggleSymbols.ContainsKey(operation[prefix.Length..]))
                return operation[prefix.Length..];
        return null;
    }

    private static readonly string[] RecordButtonSymbols =
        { "RecordButtonFormPtr", "RecordButtonOffset", "RecordButtonPressedOffset" };
    private static readonly HashSet<string> ProjectSave = new(StringComparer.Ordinal)
        { "save_project", "save_project_as", "save_copy", "save_new_version" };
    private static readonly HashSet<string> Automation = new(StringComparer.Ordinal)
    {
        "create_automation_clip", "add_automation_clip", "set_automation_points", "add_automation_point",
        "delete_automation_point", "set_automation_point", "list_automation_points", "query_automation_points"
    };

    internal static IReadOnlyList<string> Requirements(string operation)
    {
        if (RawMixer.Contains(operation)) return new[] { "mixer_layout" };
        if (Chat.Contains(operation)) return new[] { "legacy_browser_ui" };
        if (ConditionalMixer.Contains(operation)) return new[] { "mixer_layout_for_effect_slot" };
        if (Timeline.Contains(operation)) return new[] { "timeline_layout" };
        if (MixerArm.Contains(operation)) return new[] { "mixer_arm_symbols" };
        if (RecordingFilter.Contains(operation)) return new[] { "recording_filter_symbols" };
        if (operation == "get_record_pressed") return new[] { "record_button_symbols" };
        if (operation == "get_recording_active") return new[] { "recording_state_symbols" };
        if (ToggleKey(operation) is not null) return new[] { "transport_toggle_symbols" };
        if (Automation.Contains(operation)) return new[] { "automation_clips" };
        if (ProjectSave.Contains(operation) || StateSnapshot.Contains(operation)) return new[] { "project_note_validation" };
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
        if (GatedUnavailable(operation, status) is { } gatedError) return gatedError;
        if (Chat.Contains(operation) && status.FileVersion != "25.2.5.5319")
            return "The legacy browser UI layout is verified only for FL Studio 25.2.5.5319.";
        return null;
    }

    /// <summary>Operations gated on an exact-build layout or on catalogued symbols that may not resolve.</summary>
    private static string? GatedUnavailable(string operation, FlSymbolStatus status)
    {
        if (Timeline.Contains(operation) && status.TimelineLayout is null)
            return "This operation requires a verified timeline layout for the running FL Studio build.";
        if (MixerArm.Contains(operation) && (status.MixerLayout is null || MixerArmSymbols.Any(status.Unresolved.Contains)))
            return "The native mixer record-arm symbols (FLmx_SetTrackArmed/MixerTrackArmedOffset) or the mixer layout are not resolved for the running FL Studio build; live per-insert capture is unavailable.";
        if (ToggleKey(operation) is { } toggle &&
            TransportToggleSymbols[toggle].Concat(TransportOptionsSymbols).Any(status.Unresolved.Contains))
            return $"The native symbols for FL's {toggle.Replace('_', ' ')} toggle are not resolved for the running FL Studio build.";
        if (operation == "get_recording_active" && status.Unresolved.Contains("RecordingActiveCountPtr"))
            return "The native recording-state symbol (RecordingActiveCountPtr) is not resolved for the running FL Studio build.";
        if (operation == "get_record_pressed" && RecordButtonSymbols.Any(status.Unresolved.Contains))
            return "The native record-button symbols (RecordButtonFormPtr/RecordButtonOffset/RecordButtonPressedOffset) are not resolved for the running FL Studio build; the transport record state cannot be read there.";
        if (RecordingFilter.Contains(operation) && RecordingFilterSymbols.Any(status.Unresolved.Contains))
            return "The native recording-filter symbols (RecordingFilterPtr/FLrec_SetRecordingFilter) are not resolved for the running FL Studio build; the record button's Recording filter has to be set by hand there.";
        return null;
    }

    private static string? SaveUnavailable(string operation, FlSymbolStatus status)
        => (ProjectSave.Contains(operation) || StateSnapshot.Contains(operation)) &&
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
