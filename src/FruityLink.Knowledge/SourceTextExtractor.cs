using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;

namespace FruityLink.Knowledge;

/// <summary>
/// Default <see cref="ISourceTextExtractor"/>: AngleSharp for HTML, PdfPig (UglyToad) for
/// PDFs, and UTF-8 decoding for Markdown/plain text and any unrecognised extension.
/// </summary>
internal sealed class SourceTextExtractor : ISourceTextExtractor
{
    private static readonly string[] HtmlExtensions = { ".html", ".htm" };

    /// <inheritdoc />
    public async Task<(string Text, string? Title)> ExtractHtmlAsync(string html, CancellationToken ct = default)
    {
        var parser = new HtmlParser();
        using IDocument document = await parser.ParseDocumentAsync(html, ct).ConfigureAwait(false);

        // Capture the title BEFORE stripping <head> (the <title> element lives inside it).
        string? title = document.Title;
        if (string.IsNullOrWhiteSpace(title))
            title = null;

        // Remove non-content nodes before reading text.
        foreach (IElement element in document.QuerySelectorAll("script, style, noscript, template, head"))
            element.Remove();

        IElement? root = document.Body ?? document.DocumentElement;
        string text = CollapseWhitespace(root?.TextContent ?? string.Empty);

        return (text, title);
    }

    /// <inheritdoc />
    public Task<string> ExtractPdfAsync(byte[] pdf, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (pdf is null || pdf.Length == 0)
            return Task.FromResult(string.Empty);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Encrypted (user-password) or corrupt beyond lenient parsing. Surface a clear, catchable
            // message instead of letting an unexpected exception bubble up and crash the ingest.
            throw new InvalidOperationException(
                "Could not read the PDF — it may be password-protected/encrypted or corrupt.", ex);
        }

        var sb = new System.Text.StringBuilder();
        try
        {
            int pageCount;
            try { pageCount = document.NumberOfPages; }
            catch { pageCount = 0; }

            for (int p = 1; p <= pageCount; p++)
            {
                ct.ThrowIfCancellationRequested();

                string pageText;
                try { pageText = document.GetPage(p).Text; }
                catch (OperationCanceledException) { throw; }
                catch { continue; } // skip a page PdfPig can't decode (bad fonts/glyphs) — keep the rest

                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(pageText);
                }
            }
        }
        finally
        {
            document.Dispose();
        }

        // Empty result (e.g. a scanned image-only PDF) is returned as-is and ingested as an empty source.
        return Task.FromResult(CollapseWhitespace(sb.ToString()));
    }

    /// <inheritdoc />
    public async Task<string> ExtractFileAsync(string path, CancellationToken ct = default)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".pdf")
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return await ExtractPdfAsync(bytes, ct).ConfigureAwait(false);
        }

        if (Array.IndexOf(HtmlExtensions, extension) >= 0)
        {
            string html = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            (string text, _) = await ExtractHtmlAsync(html, ct).ConfigureAwait(false);
            return text;
        }

        // Markdown (.md), plain text (.txt), and anything else: treat as UTF-8 text.
        string raw = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return CollapseWhitespace(raw);
    }

    /// <summary>
    /// Produces clean, index-safe text: drops NUL bytes, control characters, lone/orphan UTF-16
    /// surrogates and Unicode non-characters — all of which can corrupt the SQLite store or break
    /// JSON serialization when the chunk is sent to the embedding API — then collapses every run of
    /// whitespace into a single space and trims the result.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new System.Text.StringBuilder(text.Length);
        bool inWhitespace = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // Whitespace, NUL and any control char (incl. tab/newline) collapse to a single space.
            if (c == '\0' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                inWhitespace = true;
                continue;
            }

            // Keep valid surrogate pairs (e.g. emoji); drop lone/orphan surrogates that would make
            // the string invalid UTF-16 and throw during JSON serialization.
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (inWhitespace && sb.Length > 0) sb.Append(' ');
                    inWhitespace = false;
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                    continue;
                }
                continue; // lone high surrogate
            }
            if (char.IsLowSurrogate(c))
                continue; // lone low surrogate

            // Drop the replacement char (garbage from bad decoding) and Unicode non-characters.
            if (c == '\uFFFD' || c == '\uFFFE' || c == '\uFFFF' || (c >= '\uFDD0' && c <= '\uFDEF'))
                continue;

            if (inWhitespace && sb.Length > 0) sb.Append(' ');
            inWhitespace = false;
            sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}
