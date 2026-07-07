using System.Text;

namespace FruityLink.Agent;

/// <summary>
/// Incremental (streaming) companion to <see cref="ThinkTagParser"/>: feed it content chunks as
/// they arrive and it hands back the (thought, text) DELTAS each chunk contributes, so reasoning
/// and answer prose can be streamed to the UI live instead of buffered until the turn ends.
///
/// <para>Chunk boundaries are hostile to marker matching — a backend can split
/// <c>&lt;think&gt;</c> anywhere (…"&lt;th" | "ink&gt;"…) — so any tail that is a PREFIX of a
/// marker is held back until the next chunk (or <see cref="Flush"/>) decides whether it was a
/// marker or ordinary text. Markers are matched case-insensitively, like the full parser.</para>
///
/// <para>Semantics vs the full parser (which remains authoritative for what lands in history and
/// <c>TurnResult</c> — this parser only shapes the LIVE deltas):</para>
/// <list type="bullet">
/// <item>well-formed blocks and the orphan-OPEN case (close never arrives) split identically:
///   once an open marker is seen, everything until the close (or stream end) is reasoning;</item>
/// <item>the orphan-CLOSE case (the open marker was eaten upstream) CANNOT be honoured
///   incrementally — the full parser reclassifies everything BEFORE the close as reasoning, but a
///   streamer has already emitted that text. The marker itself is stripped so it never shows in
///   the bubble; the already-emitted text stays visible. The authoritative split still files it
///   as reasoning in history;</item>
/// <item>leading whitespace before the first emission on each side is trimmed (the full parser
///   trims both ends; trailing trim is impossible without buffering the future — a stray trailing
///   newline in the live bubble is cosmetic and the stored text is trimmed properly);</item>
/// <item>multiple reasoning segments are separated with a newline, matching the full parser's
///   segment join.</item>
/// </list>
/// </summary>
internal sealed class ThinkTagStreamParser
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private bool _thinking;
    private string _pending = string.Empty;
    private bool _emittedThought;
    private bool _emittedText;
    /// <summary>Set when leaving a reasoning block after emitting thought, so the NEXT reasoning
    /// segment starts on its own line (the full parser joins segments with '\n').</summary>
    private bool _thoughtSeparatorPending;

    /// <summary>
    /// Consumes one streamed content chunk and returns the (Thought, Text) deltas it contributes.
    /// Either side can be empty; a chunk that ends mid-marker contributes nothing yet and is
    /// carried into the next call.
    /// </summary>
    public (string Thought, string Text) Push(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk) && _pending.Length == 0) return (string.Empty, string.Empty);

        string work = _pending + chunk;
        _pending = string.Empty;

        var thought = new StringBuilder();
        var text = new StringBuilder();
        int pos = 0;

        while (pos < work.Length)
        {
            if (_thinking)
            {
                // Inside a reasoning block only the close marker matters — an open marker inside
                // a thought is kept as thought content, same as the full parser.
                int close = work.IndexOf(Close, pos, StringComparison.OrdinalIgnoreCase);
                if (close >= 0)
                {
                    EmitThought(thought, work.AsSpan(pos, close - pos));
                    pos = close + Close.Length;
                    _thinking = false;
                    if (_emittedThought) _thoughtSeparatorPending = true;
                    continue;
                }

                pos = EmitTailWithHoldback(thought, work, pos, asThought: true);
                break;
            }

            int nextOpen = work.IndexOf(Open, pos, StringComparison.OrdinalIgnoreCase);
            int nextClose = work.IndexOf(Close, pos, StringComparison.OrdinalIgnoreCase);

            // Orphan close in answer mode: too late to reclassify already-emitted text as
            // reasoning (the full parser's orphan-close rule needs hindsight), so just strip the
            // marker — it must never render in the bubble.
            if (nextClose >= 0 && (nextOpen < 0 || nextClose < nextOpen))
            {
                EmitText(text, work.AsSpan(pos, nextClose - pos));
                pos = nextClose + Close.Length;
                continue;
            }

            if (nextOpen >= 0)
            {
                EmitText(text, work.AsSpan(pos, nextOpen - pos));
                pos = nextOpen + Open.Length;
                _thinking = true;
                continue;
            }

            pos = EmitTailWithHoldback(text, work, pos, asThought: false);
            break;
        }

        return (thought.ToString(), text.ToString());
    }

    /// <summary>
    /// Flushes any held-back tail at end of stream. A tail survived only because it LOOKED like the
    /// start of a marker; the stream ending proves it wasn't one, so it belongs to the current side
    /// (thought while inside an open block — matching the full parser's orphan-open rule).
    /// </summary>
    public (string Thought, string Text) Flush()
    {
        if (_pending.Length == 0) return (string.Empty, string.Empty);
        string tail = _pending;
        _pending = string.Empty;

        var sb = new StringBuilder();
        if (_thinking) EmitThought(sb, tail);
        else EmitText(sb, tail);
        return _thinking ? (sb.ToString(), string.Empty) : (string.Empty, sb.ToString());
    }

    /// <summary>
    /// Emits the tail of <paramref name="work"/> minus any suffix that could be the start of a
    /// marker split across chunks — that suffix is carried into the next <see cref="Push"/>.
    /// A COMPLETE marker can never hide in the tail: the caller's IndexOf already ruled it out.
    /// </summary>
    private int EmitTailWithHoldback(StringBuilder sb, string work, int pos, bool asThought)
    {
        int holdback = 0;
        int maxLen = Math.Min(Close.Length - 1, work.Length - pos);
        for (int len = maxLen; len >= 1; len--)
        {
            if (IsMarkerPrefix(work.AsSpan(work.Length - len)))
            {
                holdback = len;
                break;
            }
        }

        var emit = work.AsSpan(pos, work.Length - pos - holdback);
        if (asThought) EmitThought(sb, emit);
        else EmitText(sb, emit);

        _pending = holdback > 0 ? work[^holdback..] : string.Empty;
        return work.Length;
    }

    /// <summary>True when <paramref name="s"/> is a PROPER prefix of either marker (case-insensitive).</summary>
    private static bool IsMarkerPrefix(ReadOnlySpan<char> s) =>
        (s.Length < Open.Length && Open.AsSpan(0, s.Length).Equals(s, StringComparison.OrdinalIgnoreCase))
        || (s.Length < Close.Length && Close.AsSpan(0, s.Length).Equals(s, StringComparison.OrdinalIgnoreCase));

    private void EmitThought(StringBuilder sb, ReadOnlySpan<char> segment)
    {
        if (!_emittedThought)
        {
            segment = segment.TrimStart();
            if (segment.IsEmpty) return;
            _emittedThought = true;
        }
        else if (_thoughtSeparatorPending)
        {
            segment = segment.TrimStart();
            if (segment.IsEmpty) return;
            sb.Append('\n');
            _thoughtSeparatorPending = false;
        }
        sb.Append(segment);
    }

    private void EmitText(StringBuilder sb, ReadOnlySpan<char> segment)
    {
        if (!_emittedText)
        {
            segment = segment.TrimStart();
            if (segment.IsEmpty) return;
            _emittedText = true;
        }
        sb.Append(segment);
    }
}
