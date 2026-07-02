namespace FruityLink.Knowledge;

/// <summary>
/// Extracts readable plain text (and, for HTML, a title) from raw source bytes. Isolated
/// behind an interface so the HTML/PDF/text strategies can be swapped or faked in tests.
/// </summary>
internal interface ISourceTextExtractor
{
    /// <summary>
    /// Extracts text from HTML markup, stripping non-content elements such as
    /// <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> and collapsing whitespace.
    /// </summary>
    /// <param name="html">The HTML markup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The readable text and the document title (or <c>null</c> if none).</returns>
    Task<(string Text, string? Title)> ExtractHtmlAsync(string html, CancellationToken ct = default);

    /// <summary>Extracts text from a PDF byte stream, page by page.</summary>
    /// <param name="pdf">The raw PDF bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> ExtractPdfAsync(byte[] pdf, CancellationToken ct = default);

    /// <summary>
    /// Extracts text for a local file, dispatching on its extension (<c>.html/.htm</c>,
    /// <c>.pdf</c>, or treating everything else as UTF-8 text).
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> ExtractFileAsync(string path, CancellationToken ct = default);
}
