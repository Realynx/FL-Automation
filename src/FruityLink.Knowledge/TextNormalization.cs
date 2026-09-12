namespace FruityLink.Knowledge;

/// <summary>
/// Shared whitespace-collapse state machine used by <see cref="TextChunker"/> (plain collapse)
/// and <see cref="SourceTextExtractor"/> (collapse + sanitize). One implementation, two modes,
/// so the two callers cannot drift apart.
/// </summary>
internal static class TextNormalization
{
    /// <summary>
    /// Collapses runs of whitespace to single spaces, preserving paragraph intent loosely.
    /// All other characters pass through untouched and the result is not trimmed.
    /// </summary>
    public static string CollapseWhitespace(string text) => Collapse(text, sanitize: false);

    /// <summary>
    /// Produces clean, index-safe text: drops NUL bytes, control characters, lone/orphan UTF-16
    /// surrogates and Unicode non-characters — all of which can corrupt the SQLite store or break
    /// JSON serialization when the chunk is sent to the embedding API — then collapses every run of
    /// whitespace into a single space and trims the result.
    /// </summary>
    public static string SanitizeAndCollapseWhitespace(string text) => Collapse(text, sanitize: true);

    private static string Collapse(string text, bool sanitize)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        bool inWhitespace = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // Whitespace collapses to a single space; when sanitizing, NUL and any control char
            // (incl. tab/newline) collapse too.
            if (char.IsWhiteSpace(c) || (sanitize && (c == '\0' || char.IsControl(c))))
            {
                inWhitespace = true;
                continue;
            }

            if (sanitize)
            {
                // Keep valid surrogate pairs (e.g. emoji); drop lone/orphan surrogates that would
                // make the string invalid UTF-16 and throw during JSON serialization.
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        if (inWhitespace && sb.Length > 0) sb.Append(' ');
                        inWhitespace = false;
                        sb.Append(c);
                        sb.Append(text[i + 1]);
                        i++;
                    }
                    continue; // lone high surrogate
                }
                if (char.IsLowSurrogate(c))
                    continue; // lone low surrogate

                // Drop the replacement char (garbage from bad decoding) and Unicode non-characters.
                if (c == '\uFFFD' || c == '\uFFFE' || c == '\uFFFF' || (c >= '\uFDD0' && c <= '\uFDEF'))
                    continue;
            }

            if (inWhitespace && sb.Length > 0) sb.Append(' ');
            inWhitespace = false;
            sb.Append(c);
        }

        return sanitize ? sb.ToString().Trim() : sb.ToString();
    }
}
