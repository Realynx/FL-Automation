using System.Text;

namespace FruityLink.Agent;

/// <summary>
/// Splits a reply's content into (reasoning, answer) by pulling out <c>&lt;think&gt;…&lt;/think&gt;</c>
/// blocks (folded in by the HTTP handler from the backend's reasoning_content/reasoning field, or
/// emitted inline by some backends). Markers are matched case-insensitively. Tolerant of MALFORMED
/// marker pairs, because weak backends and proxies routinely drop one half:
/// <list type="bullet">
/// <item>an orphan <c>&lt;think&gt;</c> with no close → everything after it is reasoning (previously
///   the raw marker leaked into the answer text shown to the user and stored in history);</item>
/// <item>an orphan <c>&lt;/think&gt;</c> with no open (the open marker was eaten upstream) →
///   everything before it is reasoning;</item>
/// <item>mixed sequences (close-before-open, several blocks) → each segment lands on the right side.</item>
/// </list>
/// </summary>
internal static class ThinkTagParser
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    /// <summary>True when <paramref name="content"/> contains either marker — cheap pre-check so
    /// callers can skip <see cref="Split"/> on the (common) marker-free message.</summary>
    internal static bool ContainsMarker(string content) =>
        content.IndexOf(Open, StringComparison.OrdinalIgnoreCase) >= 0
        || content.IndexOf(Close, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Splits <paramref name="content"/> into (Thought, Text). When no marker is present the whole
    /// content is returned as the answer with an empty thought. Multiple reasoning segments are
    /// joined with newlines; answer segments are concatenated in order (same splice-out behaviour
    /// as the original single-block implementation).
    /// </summary>
    internal static (string Thought, string Text) Split(string content)
    {
        if (string.IsNullOrEmpty(content)) return (string.Empty, content ?? string.Empty);
        if (!ContainsMarker(content)) return (string.Empty, content);

        var thought = new StringBuilder();
        var text = new StringBuilder();
        int pos = 0;
        bool thinking = false;

        while (pos < content.Length)
        {
            if (!thinking)
            {
                int nextOpen = content.IndexOf(Open, pos, StringComparison.OrdinalIgnoreCase);
                int nextClose = content.IndexOf(Close, pos, StringComparison.OrdinalIgnoreCase);

                // Orphan close first: the matching open was eaten upstream, so the text BEFORE the
                // close marker is the backend's reasoning, not answer prose.
                if (nextClose >= 0 && (nextOpen < 0 || nextClose < nextOpen))
                {
                    AppendSegment(thought, content.AsSpan(pos, nextClose - pos));
                    pos = nextClose + Close.Length;
                    continue;
                }

                if (nextOpen < 0)
                {
                    text.Append(content, pos, content.Length - pos);
                    break;
                }

                text.Append(content, pos, nextOpen - pos);
                pos = nextOpen + Open.Length;
                thinking = true;
            }
            else
            {
                int nextClose = content.IndexOf(Close, pos, StringComparison.OrdinalIgnoreCase);
                if (nextClose < 0)
                {
                    // Orphan open: the close marker never arrived (truncated reasoning stream).
                    // Everything after the open is reasoning — do NOT surface the marker or the
                    // half-finished reasoning as answer text.
                    AppendSegment(thought, content.AsSpan(pos));
                    break;
                }

                AppendSegment(thought, content.AsSpan(pos, nextClose - pos));
                pos = nextClose + Close.Length;
                thinking = false;
            }
        }

        return (thought.ToString().Trim(), text.ToString().Trim());
    }

    /// <summary>Appends a reasoning segment, separating multiple segments with a newline.</summary>
    private static void AppendSegment(StringBuilder sb, ReadOnlySpan<char> segment)
    {
        segment = segment.Trim();
        if (segment.IsEmpty) return;
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(segment);
    }
}
