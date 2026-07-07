namespace FruityLink.Knowledge;

/// <summary>
/// Splits raw source text into overlapping chunks suitable for embedding. Prefers to break
/// on sentence or whitespace boundaries so chunks never split mid-word, and carries a small
/// overlap between consecutive chunks to preserve context across boundaries. Pure and
/// deterministic.
/// </summary>
internal sealed class TextChunker
{
    /// <summary>Default target chunk size in characters.</summary>
    public const int DefaultMaxChars = 800;

    /// <summary>Default overlap between consecutive chunks in characters.</summary>
    public const int DefaultOverlapChars = 150;

    private readonly int _maxChars;
    private readonly int _overlapChars;

    /// <summary>Creates a chunker with the given target size and overlap.</summary>
    /// <param name="maxChars">Maximum chunk length in characters (must be positive).</param>
    /// <param name="overlapChars">
    /// Characters of overlap to carry from the end of one chunk into the next
    /// (must be non-negative and less than <paramref name="maxChars"/>).
    /// </param>
    public TextChunker(int maxChars = DefaultMaxChars, int overlapChars = DefaultOverlapChars)
    {
        if (maxChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars), maxChars, "Must be positive.");
        if (overlapChars < 0 || overlapChars >= maxChars)
            throw new ArgumentOutOfRangeException(nameof(overlapChars), overlapChars, "Must be in [0, maxChars).");

        _maxChars = maxChars;
        _overlapChars = overlapChars;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into chunks. Empty/whitespace-only chunks (and input)
    /// produce no output.
    /// </summary>
    public IReadOnlyList<string> Chunk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        // Normalise so boundary detection is predictable; embedding does not need raw layout.
        string normalized = NormalizeWhitespace(text);
        if (normalized.Length == 0)
            return Array.Empty<string>();

        var chunks = new List<string>();
        int start = 0;
        while (start < normalized.Length)
        {
            int remaining = normalized.Length - start;
            if (remaining <= _maxChars)
            {
                AddIfNotBlank(chunks, normalized[start..]);
                break;
            }

            int hardEnd = start + _maxChars;
            int breakAt = FindBreak(normalized, start, hardEnd);

            AddIfNotBlank(chunks, normalized[start..breakAt]);

            // Advance, keeping ~overlap characters, but ALWAYS make forward progress. The overlap
            // alignment walks back to the start of a word; on a long UNBROKEN token (e.g. a 100+
            // char URL) that walk can land at or before `start`, which would re-emit the same
            // window forever (an infinite loop / CPU spin). breakAt is always > start, so fall back
            // to it whenever the aligned overlap fails to advance.
            int next = breakAt - _overlapChars;
            if (next <= start)
                next = breakAt;
            int aligned = AlignOverlapToWordStart(normalized, next, breakAt);
            start = aligned > start ? aligned : breakAt;
        }

        return chunks;
    }

    /// <summary>
    /// Finds the best break index in <c>(start, hardEnd]</c>, preferring a sentence-ending
    /// boundary, then any whitespace, falling back to the hard limit so we never split a word
    /// unless the word itself is longer than the chunk size.
    /// </summary>
    private static int FindBreak(string s, int start, int hardEnd)
    {
        // The minimum acceptable break keeps chunks from collapsing to almost nothing.
        int minBreak = start + Math.Max(1, (hardEnd - start) / 2);

        int sentenceBreak = -1;
        int whitespaceBreak = -1;
        for (int i = hardEnd - 1; i >= minBreak; i--)
        {
            char c = s[i];
            if (sentenceBreak < 0 && IsSentenceEnd(c) && (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])))
                sentenceBreak = i + 1; // break after the punctuation
            if (whitespaceBreak < 0 && char.IsWhiteSpace(c))
                whitespaceBreak = i; // break at the space (it is trimmed away)
            if (sentenceBreak >= 0)
                break;
        }

        if (sentenceBreak >= 0)
            return sentenceBreak;
        if (whitespaceBreak >= 0)
            return whitespaceBreak;

        // No boundary found within the window: the run is a single long token. Break exactly
        // at the hard limit (mid-token) — unavoidable for pathological input.
        return hardEnd;
    }

    /// <summary>
    /// Pushes the overlap start to the beginning of a word so the next chunk does not start in
    /// the middle of a token. Never advances past <paramref name="breakAt"/>.
    /// </summary>
    private static int AlignOverlapToWordStart(string s, int candidate, int breakAt)
    {
        if (candidate <= 0)
            return 0;
        if (candidate >= breakAt)
            return breakAt;

        // If we are inside a word (previous char is non-whitespace), walk back to the word start.
        if (!char.IsWhiteSpace(s[candidate]) && !char.IsWhiteSpace(s[candidate - 1]))
        {
            int i = candidate;
            while (i > 0 && !char.IsWhiteSpace(s[i - 1]))
                i--;
            candidate = i;
        }

        // Skip leading whitespace so the chunk starts on visible text.
        while (candidate < breakAt && char.IsWhiteSpace(s[candidate]))
            candidate++;

        return candidate;
    }

    private static void AddIfNotBlank(List<string> chunks, string chunk)
    {
        string trimmed = chunk.Trim();
        if (trimmed.Length > 0)
            chunks.Add(trimmed);
    }

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?';

    /// <summary>Collapses runs of whitespace to single spaces, preserving paragraph intent loosely.</summary>
    private static string NormalizeWhitespace(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool inWhitespace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && sb.Length > 0)
                sb.Append(' ');
            inWhitespace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
