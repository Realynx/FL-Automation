using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using RmConverter = ReverseMarkdown.Converter;

namespace FruityLink.ManualIngest;

/// <summary>
/// Phase 2 — converts the crawled HTML mirror to Markdown. Isolates the main content column
/// (<c>#rightcol</c>) from the Help &amp; Manual template, drops the injected navigation frame
/// and scripts/styles, then runs ReverseMarkdown. Each page is prefixed with its title and
/// source URL so the downstream chunks stay attributable.
/// </summary>
public sealed class HtmlToMarkdown
{
    private readonly ManualPaths _paths;
    private readonly HtmlParser _parser = new();
    private readonly RmConverter _converter = new(new ReverseMarkdown.Config
    {
        UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Bypass,
        GithubFlavored = true,
        RemoveComments = true,
        SmartHrefHandling = true,
    });

    public HtmlToMarkdown(ManualPaths paths) => _paths = paths;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<PageEntry> pages = ManualManifest.Read<PageEntry>(_paths.ManifestFile);
        Directory.CreateDirectory(_paths.MarkdownDir);
        Console.WriteLine($"Converting {pages.Count} page(s) to Markdown…");

        var index = new List<MarkdownEntry>();
        int converted = 0, empty = 0;
        foreach (PageEntry page in pages)
        {
            ct.ThrowIfCancellationRequested();
            string htmlFile = Path.Combine(_paths.HtmlDir, page.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(htmlFile)) { Console.WriteLine($"  ! missing {page.RelPath}"); continue; }

            string html = await File.ReadAllTextAsync(htmlFile, ct).ConfigureAwait(false);
            string body = await ExtractContentHtmlAsync(html, ct).ConfigureAwait(false);
            string markdown = _converter.Convert(body).Trim();
            if (markdown.Length == 0) { empty++; continue; }

            string doc = $"# {page.Title}\n\nSource: {page.Url}\n\n{markdown}\n";
            string mdRel = Path.ChangeExtension(page.RelPath, ".md").Replace('/', Path.DirectorySeparatorChar);
            string mdFull = Path.Combine(_paths.MarkdownDir, mdRel);
            Directory.CreateDirectory(Path.GetDirectoryName(mdFull)!);
            await File.WriteAllTextAsync(mdFull, doc, ct).ConfigureAwait(false);

            index.Add(new MarkdownEntry(mdRel.Replace('\\', '/'), page.Url, page.Title));
            converted++;
        }

        index.Sort((a, b) => string.CompareOrdinal(a.MdPath, b.MdPath));
        ManualManifest.Write(_paths.MarkdownIndexFile, index);

        Console.WriteLine();
        Console.WriteLine($"Convert complete: {converted} markdown file(s), {empty} skipped (empty).");
        Console.WriteLine($"  Markdown : {_paths.MarkdownDir}");
        Console.WriteLine($"  Index    : {_paths.MarkdownIndexFile}");
        return converted;
    }

    /// <summary>
    /// Returns the inner HTML of the main content node (<c>#rightcol</c>) with navigation,
    /// scripts and styles removed. Falls back to the whole body when the template node is absent.
    /// </summary>
    private async Task<string> ExtractContentHtmlAsync(string html, CancellationToken ct)
    {
        IDocument doc = await _parser.ParseDocumentAsync(html, ct).ConfigureAwait(false);
        IElement? content = doc.QuerySelector("#rightcol") ?? doc.Body;
        if (content is null) return string.Empty;

        // Strip the injected TOC frame + non-content elements before conversion.
        foreach (IElement el in content.QuerySelectorAll(
                     "#leftFrame, iframe, script, style, noscript, nav"))
        {
            el.Remove();
        }
        return content.InnerHtml;
    }
}
