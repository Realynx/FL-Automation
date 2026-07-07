namespace FruityLink.ManualIngest;

/// <summary>
/// Canonical on-disk layout for the manual corpus, all rooted under one working directory
/// (default "FL Studio online manual"). Keeps the raw HTML mirror, the converted markdown,
/// the crawl manifest, and the built vector DB co-located and predictable across the three
/// pipeline stages (crawl → convert → build).
/// </summary>
public sealed class ManualPaths
{
    public ManualPaths(string root)
    {
        Root = Path.GetFullPath(root);
        HtmlDir = Path.Combine(Root, "html");
        MarkdownDir = Path.Combine(Root, "markdown");
        ManifestFile = Path.Combine(Root, "manifest.json");
        MarkdownIndexFile = Path.Combine(MarkdownDir, "index.json");
        DbFile = Path.Combine(Root, "fl-manual.db");
    }

    /// <summary>The working-directory root (holds every artifact below).</summary>
    public string Root { get; }

    /// <summary>Raw crawled HTML, mirroring the site's <c>html/…</c> path layout.</summary>
    public string HtmlDir { get; }

    /// <summary>Converted markdown, mirroring the same relative layout with <c>.md</c> names.</summary>
    public string MarkdownDir { get; }

    /// <summary>Crawl output: page URL/path/title records (written by <c>crawl</c>).</summary>
    public string ManifestFile { get; }

    /// <summary>Convert output: markdown path/URL/title records (written by <c>convert</c>).</summary>
    public string MarkdownIndexFile { get; }

    /// <summary>Build output: the shipped SQLite vector database.</summary>
    public string DbFile { get; }
}
