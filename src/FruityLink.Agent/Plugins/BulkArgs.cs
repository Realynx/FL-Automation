using System.Text.Json;
using FruityLink.Core.Abstractions;
using static FruityLink.Agent.Plugins.PluginSupport;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Bulk-argument parsing for <see cref="NativeControlPlugin"/>'s batchable tools (single OR multiple
/// items in one string). Those tools take a SINGLE string arg that is parsed leniently, because weak
/// backends vary wildly in how they emit a list. Index lists accept CSV or a JSON array; object
/// lists (moves/resizes/adds/params) accept a JSON array, a bare single object, and case-insensitive
/// field names with a few aliases. On a hard parse failure the parser returns a clear error naming
/// the offending item so the model can self-correct instead of retrying blind.
/// </summary>
internal static class BulkArgs
{
    private static readonly JsonDocumentOptions LenientJson =
        new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Parse the native_add_notes string ('key,start,length[,velocity[,channel]]' per note,
    /// separated by ';' or newlines) into NoteSpecs. Tolerant by design for weak backends: accepts
    /// decimals (rounded), treats velocity as optional (defaults to 100), and SKIPS malformed lines
    /// (returned separately) instead of throwing away the whole batch — one typo shouldn't lose a
    /// 32-note melody. Notes without a channel field use <paramref name="defaultChannel"/>.</summary>
    internal static (List<NoteSpec> Parsed, List<string> Skipped) ParseNotes(string notes, int defaultChannel)
    {
        var list = new List<NoteSpec>();
        var skipped = new List<string>();
        if (string.IsNullOrWhiteSpace(notes)) return (list, skipped);
        foreach (var item in notes.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = item.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 3 || !TryNum(f[0], out int key) || !TryNum(f[1], out int start) || !TryNum(f[2], out int len))
            {
                skipped.Add(item);
                continue;
            }
            int vel = 100;
            if (f.Length >= 4 && f[3].Length > 0 && !TryNum(f[3], out vel)) { skipped.Add(item); continue; }
            int chan = defaultChannel;
            if (f.Length >= 5 && f[4].Length > 0 && !TryNum(f[4], out chan)) { skipped.Add(item); continue; }
            list.Add(new NoteSpec(chan, key, start, len, vel));
        }
        return (list, skipped);
    }

    /// <summary>Parse an index list: a JSON array ("[0,2,5]"), a bare CSV ("0,2,5"), or a single value
    /// ("3"). Tolerates surrounding brackets, and comma/space/semicolon separators. Returns false with
    /// <paramref name="bad"/> = the first non-numeric token (empty when simply nothing was provided).</summary>
    internal static bool TryParseIndexList(string raw, out List<int> result, out string bad)
    {
        result = new List<int>();
        bad = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim().Trim('[', ']', '(', ')');
        foreach (var tok in s.Split(new[] { ',', ' ', '\t', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNum(tok, out int v)) result.Add(v);
            else { bad = tok; result.Clear(); return false; }
        }
        return result.Count > 0;
    }

    /// <summary>Normalize a JSON object-or-array string into a list of object elements. Returns the parse
    /// error (or null on success). The caller must keep <paramref name="doc"/> alive while reading elements.</summary>
    private static string? ParseObjectList(string raw, out JsonDocument? doc, out List<JsonElement> objs)
    {
        doc = null;
        objs = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(raw)) return "empty input — provide a JSON array of objects";
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(raw, LenientJson); }
        catch (JsonException ex) { return $"not valid JSON ({ex.Message})"; }
        var root = parsed.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) { parsed.Dispose(); return "each list entry must be a JSON object like {\"index\":0,...}"; }
                objs.Add(e);
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            objs.Add(root);   // tolerate a single bare object as a 1-element list
        }
        else { parsed.Dispose(); return "expected a JSON array of objects (or a single object)"; }
        doc = parsed;
        return null;
    }

    /// <summary>Read a numeric field by any of <paramref name="names"/> (case-insensitive), accepting a JSON
    /// number or a numeric string. Returns false if absent or non-numeric.</summary>
    private static bool TryGetNum(JsonElement o, out double value, params string[] names)
    {
        value = 0;
        foreach (var prop in o.EnumerateObject())
        {
            if (!names.Any(n => string.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
            var v = prop.Value;
            if (v.ValueKind == JsonValueKind.Number) { value = v.GetDouble(); return true; }
            if (v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value))
                return true;
            return false;   // present but wrong type
        }
        return false;
    }

    private static bool TryGetInt(JsonElement o, out int value, params string[] names)
    {
        value = 0;
        if (!TryGetNum(o, out double d, names)) return false;
        value = (int)Math.Round(d);
        return true;
    }

    /// <summary>Read a boolean field by any of <paramref name="names"/> (case-insensitive), accepting a JSON
    /// bool, the strings "true"/"false", or a number (0 = false). Returns false when absent/unrecognized.</summary>
    private static bool TryGetBool(JsonElement o, out bool value, params string[] names)
    {
        value = false;
        foreach (var prop in o.EnumerateObject())
        {
            if (!names.Any(n => string.Equals(prop.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
            var v = prop.Value;
            switch (v.ValueKind)
            {
                case JsonValueKind.True: value = true; return true;
                case JsonValueKind.False: value = false; return true;
                case JsonValueKind.Number: value = v.GetDouble() != 0; return true;
                case JsonValueKind.String when bool.TryParse(v.GetString(), out value): return true;
                default: return false;
            }
        }
        return false;
    }

    /// <summary>Parse the native_delete_notes arg into NoteRefs — each entry identifies a note by
    /// channel + key + pos (the triple native_get_notes shows). JSON array or a single bare object.</summary>
    internal static (List<NoteRef> Parsed, string? Err) ParseNoteRefs(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<NoteRef>(), $"notes: {err}");
            var list = new List<NoteRef>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int ch, "channel", "chan", "ch"))
                    return (list, $"note #{i + 1} is missing a numeric 'channel'");
                if (!TryGetInt(objs[i], out int key, "key", "pitch", "note"))
                    return (list, $"note #{i + 1} is missing a numeric 'key'");
                if (!TryGetInt(objs[i], out int pos, "pos", "start", "startTick", "tick", "position"))
                    return (list, $"note #{i + 1} is missing a numeric 'pos'");
                list.Add(new NoteRef(ch, key, pos));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the native_edit_notes arg into NoteEdits: channel + key + pos LOCATE the note (its
    /// current values); newKey/newStart/newLength/newVelocity/muted are the new values (omit = unchanged).
    /// An entry that changes nothing is an error (nudges the model to include at least one new field).</summary>
    internal static (List<NoteEdit> Parsed, string? Err) ParseNoteEdits(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<NoteEdit>(), $"edits: {err}");
            var list = new List<NoteEdit>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int ch, "channel", "chan", "ch"))
                    return (list, $"edit #{i + 1} is missing a numeric 'channel'");
                if (!TryGetInt(objs[i], out int key, "key", "pitch", "note"))
                    return (list, $"edit #{i + 1} is missing a numeric 'key'");
                if (!TryGetInt(objs[i], out int pos, "pos", "start", "startTick", "tick", "position"))
                    return (list, $"edit #{i + 1} is missing a numeric 'pos'");
                int? newKey = TryGetInt(objs[i], out int nk, "newKey", "toKey", "keyTo") ? nk : null;
                int? newStart = TryGetInt(objs[i], out int ns, "newStart", "newPos", "newStartTick", "toStart") ? ns : null;
                int? newLen = TryGetInt(objs[i], out int nl, "newLength", "newLen", "length", "len") ? nl : null;
                int? newVel = TryGetInt(objs[i], out int nv, "newVelocity", "newVel", "velocity", "vel") ? nv : null;
                bool? muted = TryGetBool(objs[i], out bool m, "muted", "mute") ? m : null;
                if (newKey is null && newStart is null && newLen is null && newVel is null && muted is null)
                    return (list, $"edit #{i + 1} changes nothing — set at least one of newKey/newStart/newLength/newVelocity/muted");
                list.Add(new NoteEdit(ch, key, pos, newKey, newStart, newLen, newVel, muted));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the native_move_clips arg into ClipMoves. Requires index + start; track is optional
    /// (missing or &lt;= 0 = keep current, encoded as -1).</summary>
    internal static (List<ClipMove> Parsed, string? Err) ParseMoves(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<ClipMove>(), $"moves: {err}");
            var list = new List<ClipMove>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int index, "index", "idx", "clip", "clipIndex", "slot"))
                    return (list, $"move #{i + 1} is missing a numeric 'index'");
                if (!TryGetInt(objs[i], out int start, "start", "startTick", "tick", "pos", "position"))
                    return (list, $"move #{i + 1} is missing a numeric 'start'");
                int track = TryGetInt(objs[i], out int t, "track", "trk") && t > 0 ? t : -1;
                list.Add(new ClipMove(index, start, track));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the native_resize_clips arg into ClipResizes (index + length required).</summary>
    internal static (List<ClipResize> Parsed, string? Err) ParseResizes(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<ClipResize>(), $"resizes: {err}");
            var list = new List<ClipResize>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int index, "index", "idx", "clip", "clipIndex", "slot"))
                    return (list, $"resize #{i + 1} is missing a numeric 'index'");
                if (!TryGetInt(objs[i], out int length, "length", "len", "lengthTick"))
                    return (list, $"resize #{i + 1} is missing a numeric 'length'");
                list.Add(new ClipResize(index, length));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the native_add_pattern_clips arg into PatternClipSpecs (pattern + track + start
    /// required; length optional, defaults 0 = the pattern's own length).</summary>
    internal static (List<PatternClipSpec> Parsed, string? Err) ParseClipSpecs(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<PatternClipSpec>(), $"clips: {err}");
            var list = new List<PatternClipSpec>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int pattern, "pattern", "pat", "patternIndex"))
                    return (list, $"clip #{i + 1} is missing a numeric 'pattern'");
                if (!TryGetInt(objs[i], out int track, "track", "trk"))
                    return (list, $"clip #{i + 1} is missing a numeric 'track'");
                if (!TryGetInt(objs[i], out int start, "start", "startTick", "tick", "pos", "position"))
                    return (list, $"clip #{i + 1} is missing a numeric 'start'");
                int length = TryGetInt(objs[i], out int len, "length", "len", "lengthTick") ? len : 0;
                list.Add(new PatternClipSpec(pattern, track, start, length));
            }
            return (list, null);
        }
    }

    /// <summary>Parse the plugin-param arg into (index, value) pairs (both required per entry).</summary>
    internal static (List<(int Index, double Value)> Parsed, string? Err) ParseParams(string raw)
    {
        var err = ParseObjectList(raw, out var doc, out var objs);
        using (doc)
        {
            if (err is not null) return (new List<(int, double)>(), $"params: {err}");
            var list = new List<(int, double)>();
            for (int i = 0; i < objs.Count; i++)
            {
                if (!TryGetInt(objs[i], out int index, "index", "idx", "param", "paramIndex", "i"))
                    return (list, $"param #{i + 1} is missing a numeric 'index'");
                if (!TryGetNum(objs[i], out double value, "value", "val", "v"))
                    return (list, $"param #{i + 1} is missing a numeric 'value'");
                list.Add((index, value));
            }
            return (list, null);
        }
    }
}
