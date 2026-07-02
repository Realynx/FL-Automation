using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Agent;

/// <summary>
/// Per-turn tool subsetting: picks WHICH of the kernel's registered tools are ADVERTISED to the
/// model for one request, based on FL-domain keywords in the user's message (+ a little recent
/// history). The kernel keeps ALL plugins registered — invocation is unaffected — this only shrinks
/// the request's tool schema, which is the single biggest prompt-token cost on an ~84-tool surface
/// and the main thing that overwhelms small models' tool choice.
///
/// <para>Selection = an always-on CORE set (every read/list/get tool, the music-theory trio,
/// knowledge search, and run_parallel_tasks where registered — i.e. main agent only, since
/// sub-agent kernels don't register the Orchestration plugin) UNIONED with each keyword-matched
/// domain group's action tools. Tool names are resolved against <c>kernel.Plugins</c> at runtime:
/// what exists is advertised, what doesn't is skipped — robust to other workstreams changing the
/// advertised surface.</para>
///
/// <para>Fail open, never closed: when nothing matches — or the PREVIOUS turn shows the model asked
/// for a tool that wasn't advertised (SK's "wasn't defined" tool-error) — return null, meaning
/// "advertise the FULL surface". A wrong subset costs one wasted round; a full surface only costs
/// tokens.</para>
/// </summary>
internal static class ToolSubsetSelector
{
    /// <summary>
    /// Fragments of the tool-message error SK's OpenAI function-calls processor records when the
    /// model requested a function outside the advertised list (or missing from the kernel). Seeing
    /// one in the previous turn means our subset guessed wrong — fall back to the full surface.
    /// </summary>
    private static readonly string[] UndefinedFunctionErrorFragments =
    {
        "wasn't defined",
        "could not be found",
    };

    /// <summary>Always-advertised names that aren't covered by the read/list/get prefix rule.</summary>
    private static readonly string[] CoreToolNames =
    {
        "get_creative_soul",
        "list_scales",
        "build_chord_progression",
        "search_knowledge",
        "run_parallel_tasks",   // resolves only on the main agent (sub-agents don't register it)
        // native_set_tempo is edit-distance-1 from native_get_tempo, which the CoreReadPrefixes rule
        // ALWAYS advertises. The wire-layer tool-name resolver (LlmToolCallRepairHandler) rewrites a
        // response name that is one edit away from exactly one ADVERTISED tool — so if a subset ever
        // advertised get_tempo without set_tempo, a real, correctly-spelled set_tempo call from the
        // model would be silently rewritten into get_tempo (and "succeed", bypassing the
        // wasn't-defined fallback). Keeping the pair co-advertised makes that rewrite impossible;
        // ToolSurfaceEditDistanceTests ratchets this for any future distance-1 pair.
        "native_set_tempo",
    };

    /// <summary>Read-tool prefix rule: every native read is always advertised so the model can map
    /// names → indices for ANY request without a re-advertise round. (native_is_available doesn't
    /// match on purpose — it is diagnostics-only and being un-advertised.)</summary>
    private static readonly string[] CoreReadPrefixes = { "native_get_", "native_list_" };

    /// <summary>One FL work domain: keyword vocabulary → the ACTION tools it unlocks.</summary>
    private sealed record ToolGroup(string[] Keywords, string[] Tools);

    // Keyword rules (see Matches): multi-word keywords are substring-matched; keywords of <= 3 chars
    // must equal a whole word; longer ones prefix-match a word ("drum" hits "drums"). All
    // case-insensitive. Overlap between groups is fine — the union is what gets advertised.
    private static readonly ToolGroup[] Groups =
    {
        // Mixer / levels / FX chain
        new(new[]
            {
                "mixer", "volume", "pan", "panning", "eq", "equalizer", "send", "sends", "effect",
                "effects", "fx", "insert", "reverb", "delay", "echo", "compressor", "compress",
                "compression", "limiter", "sidechain", "master", "gain", "level", "levels", "route",
                "routing", "mute", "unmute", "loud", "louder", "quiet", "quieter", "mix",
            },
            new[]
            {
                "native_set_mixer_volume", "native_set_mixer_pan", "native_set_mixer_send",
                "native_set_mixer_eq_gain", "native_add_mixer_effect", "native_remove_mixer_effect",
                "native_clone_mixer_effect", "native_route_channel_to_mixer",
                // Master volume is native_set_mixer_volume track 0 (native_set_master_volume was
                // pruned from the surface as a duplicate entry).
                "native_set_master_pitch",
                "native_set_channel_volume", "native_set_channel_pan", "native_set_channel_muted",
                "native_set_mixer_plugin_params",
            }),

        // Channels / instruments / generators
        new(new[]
            {
                "channel", "channels", "instrument", "instruments", "synth", "synths", "plugin",
                "plugins", "generator", "load", "sytrus", "flex", "boobass", "serum", "vst", "piano",
                "keys", "pad", "lead", "pluck",
            },
            new[]
            {
                "native_add_channel", "native_add_sample_channel", "native_set_channel_volume",
                "native_set_channel_pan", "native_set_channel_pitch", "native_set_channel_muted",
                "native_select_channel", "native_route_channel_to_mixer",
                "native_replace_channel_sample",
            }),

        // Notes / piano roll / patterns
        new(new[]
            {
                "note", "notes", "melody", "melodies", "chord", "chords", "drum", "drums", "pattern",
                "patterns", "piano", "roll", "bass", "bassline", "riff", "arp", "arpeggio",
                "progression", "scale", "scales", "beat", "beats", "groove", "hit", "hits",
                "quantize", "transpose", "velocity",
            },
            new[]
            {
                "native_add_note", "native_add_notes", "native_create_pattern",
                "native_clear_pattern", "native_select_pattern",
            }),

        // Playlist / arrangement / song structure
        new(new[]
            {
                "playlist", "arrangement", "arrangements", "arrange", "clip", "clips", "track",
                "tracks", "song", "section", "sections", "verse", "chorus", "drop", "intro", "outro",
                "buildup", "breakdown", "chop", "slice", "structure",
            },
            new[]
            {
                "native_add_pattern_clips", "native_move_clips", "native_resize_clips",
                "native_delete_clips", "native_mute_clips", "native_slice_clip",
                "native_duplicate_clip", "native_set_track_name", "native_set_track_color",
                "native_set_track_mute", "native_set_track_collapsed", "native_select_track",
                "native_make_arrangement", "native_clone_arrangement", "native_rename_arrangement",
                "native_delete_arrangement", "native_select_arrangement", "native_set_song_mode",
                "native_add_marker",
            }),

        // Transport / tempo
        new(new[]
            {
                "play", "playing", "stop", "record", "recording", "tempo", "bpm", "seek", "playhead",
                "pause", "shuffle", "swing", "faster", "slower", "speed",
            },
            new[]
            {
                "native_transport_play", "native_transport_stop", "native_transport_toggle_record",
                "native_set_tempo", "native_set_shuffle", "native_seek", "native_set_song_mode",
            }),

        // Plugin params / sound design
        new(new[]
            {
                "param", "params", "parameter", "parameters", "preset", "cutoff", "filter",
                "resonance", "osc", "oscillator", "wavetable", "macro", "adsr", "attack", "decay",
                "sustain", "release", "lfo", "detune", "unison", "knob", "tweak", "sound design",
                "sound-design", "brighter", "darker", "warmer",
            },
            new[]
            {
                "native_set_channel_plugin_params", "native_set_mixer_plugin_params",
            }),

        // Samples / drums material
        new(new[]
            {
                "sample", "samples", "kick", "snare", "hat", "hats", "hihat", "hi-hat", "clap",
                "tom", "cymbal", "crash", "ride", "perc", "percussion", "808", "909", "loop",
                "loops", "one shot", "one-shot", "wav", "audio",
            },
            new[]
            {
                "native_add_sample_channel", "native_replace_channel_sample",
            }),

        // Project lifecycle / markers / version history
        new(new[]
            {
                "save", "saved", "project", "marker", "markers", "render", "export", "open",
                "version", "flp", "backup", "recent", "history", "undo", "redo", "revert",
                "restore", "rollback", "snapshot", "checkpoint", "compare", "diff",
            },
            new[]
            {
                "native_save_project", "native_save_project_as", "native_save_copy",
                "native_save_new_version", "native_open_project", "native_new_project",
                "native_add_marker",
                // The agent's read-only view of its OWN change history (VersioningPlugin). Not a
                // core read (they aren't native_get_/list_-prefixed on purpose): version recall is
                // an occasional need, so it rides this group + the fail-open full-surface fallback
                // instead of costing schema tokens on every turn.
                "list_versions", "get_version_changes",
            }),

        // Automation
        new(new[]
            {
                "automation", "automate", "fade", "fades", "sweep", "ramp", "envelope", "riser",
            },
            new[]
            {
                // Automation clips are CREATED via native_add_channel("Automation Clip"),
                // so the generic add-channel tool rides along with the point editors.
                "native_add_automation_point", "native_delete_automation_point",
                "native_add_channel",
            }),
    };

    /// <summary>Test hook (see ToolSurfaceEditDistanceTests): the keyword vocabulary of every domain
    /// group, so the ratchet test can generate every subset SHAPE the selector can produce without
    /// duplicating the private keyword table.</summary>
    internal static IEnumerable<string[]> GroupKeywordSets => Groups.Select(g => g.Keywords);

    /// <summary>
    /// Picks the tools to advertise for the coming turn, or null to advertise the FULL surface.
    /// Call BEFORE appending <paramref name="userMessage"/> to <paramref name="history"/> — the
    /// history's tail is read as "the previous turn". Matching runs over the user message plus the
    /// last two content-bearing history messages (so "make it louder" still matches the mixer group
    /// discussed a message ago). Returned functions come from <paramref name="kernel"/>.Plugins in
    /// registration/declaration order — stable and deterministic across turns.
    /// </summary>
    internal static IReadOnlyList<KernelFunction>? SelectForTurn(
        Kernel kernel, string userMessage, ChatHistory history)
    {
        if (PreviousTurnRequestedUndefinedFunction(history))
            return null;

        string matchText = BuildMatchText(userMessage, history);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ToolGroup group in Groups)
        {
            if (group.Keywords.Any(k => Matches(matchText, k)))
                foreach (string tool in group.Tools)
                    wanted.Add(tool);
        }

        if (wanted.Count == 0)
            return null;   // nothing recognized → don't guess; advertise everything

        foreach (string tool in CoreToolNames)
            wanted.Add(tool);

        // Resolve names → live KernelFunction instances; skip what isn't registered/advertised.
        var subset = new List<KernelFunction>();
        foreach (KernelPlugin plugin in kernel.Plugins)
            foreach (KernelFunction function in plugin)
                if (wanted.Contains(function.Name) || IsCoreRead(function.Name))
                    subset.Add(function);

        return subset.Count == 0 ? null : subset;
    }

    /// <summary>True for the always-on read/list/get tools (see <see cref="CoreReadPrefixes"/>).</summary>
    private static bool IsCoreRead(string functionName) =>
        CoreReadPrefixes.Any(p => functionName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// User message + the last two content-bearing CONVERSATIONAL (user/assistant) history messages.
    /// System and tool messages are skipped: a raw tool dump (e.g. a native_list_samples listing full
    /// of "kick"/"snare" paths) would keyword-match half the groups and defeat the subsetting.
    /// </summary>
    private static string BuildMatchText(string userMessage, ChatHistory history)
    {
        var sb = new System.Text.StringBuilder(userMessage);
        int taken = 0;
        for (int i = history.Count - 1; i >= 0 && taken < 2; i--)
        {
            ChatMessageContent m = history[i];
            if (m.Role != AuthorRole.User && m.Role != AuthorRole.Assistant) continue;
            string content = m.Content ?? string.Empty;
            if (content.Length == 0) continue;
            sb.Append('\n').Append(content);
            taken++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Keyword rule: multi-word keywords substring-match the whole text; short keywords (≤ 3 chars,
    /// e.g. "eq", "fx", "pan") must equal a whole word to avoid false hits inside other words;
    /// longer keywords prefix-match a word so plurals/inflections still hit ("drum" → "drums").
    /// </summary>
    private static bool Matches(string text, string keyword)
    {
        if (keyword.Contains(' '))
            return text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        int index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startsWord = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            int end = index + keyword.Length;
            bool endsWord = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (startsWord && (keyword.Length > 3 || endsWord))
                return true;
            index += 1;
        }
        return false;
    }

    /// <summary>
    /// Scans the PREVIOUS turn (history tail back to the last user message) for a tool message
    /// carrying SK's "function wasn't defined" error — the model tried to call something we didn't
    /// advertise, so this turn must fall back to the full surface.
    /// </summary>
    private static bool PreviousTurnRequestedUndefinedFunction(ChatHistory history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            ChatMessageContent m = history[i];
            if (m.Role == AuthorRole.User)
                break;
            if (m.Role != AuthorRole.Tool)
                continue;

            if (ContainsUndefinedError(m.Content))
                return true;
            foreach (FunctionResultContent result in m.Items.OfType<FunctionResultContent>())
                if (result.Result is string s && ContainsUndefinedError(s))
                    return true;
        }
        return false;
    }

    private static bool ContainsUndefinedError(string? text) =>
        text is not null
        && UndefinedFunctionErrorFragments.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));
}
