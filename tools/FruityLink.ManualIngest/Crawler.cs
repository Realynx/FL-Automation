using System.Net;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Html.Parser;

namespace FruityLink.ManualIngest;

/// <summary>
/// Phase 1 — a courteous breadth-first crawler for the FL Studio online manual (a Help &amp; Manual
/// webhelp export). Seeds from the table-of-contents frame, then follows internal <c>html/…</c>
/// links so cross-referenced pages that are not in the TOC (e.g. <c>html/plugins/…</c>) are also
/// captured. Saves a byte-faithful HTML mirror plus a manifest of (url, path, title). A minimum
/// 1&#160;second delay is enforced between requests as a courtesy to the server.
/// </summary>
public sealed class Crawler
{
    // The manual is embedded in a WordPress page via two iframes; the real content is a static
    // Help & Manual export under …/fl-studio-online-manual/html/. We crawl that subtree only.
    private const string SiteRoot = "https://www.image-line.com/fl-studio-learning/fl-studio-online-manual";
    private const string TocUrl = SiteRoot + "/Index_Frame_Left.html";
    private const string ContentPrefix = SiteRoot + "/html/";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120.0 Safari/537.36";

    private readonly ManualPaths _paths;
    private readonly int _delayMs;
    private readonly int _maxPages;
    private readonly HttpClient _http;
    private readonly HtmlParser _parser = new();

    public Crawler(ManualPaths paths, int delayMs, int maxPages)
    {
        _paths = paths;
        // "at least 1 second" — clamp up, never below, whatever the caller passed.
        _delayMs = Math.Max(1000, delayMs);
        _maxPages = maxPages <= 0 ? int.MaxValue : maxPages;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Referrer = new Uri(SiteRoot);
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_paths.HtmlDir);

        // Previous crawl state (for incremental re-crawl: conditional GETs + change classification).
        var prev = new Dictionary<string, PageEntry>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_paths.ManifestFile))
        {
            foreach (PageEntry e in ManualManifest.Read<PageEntry>(_paths.ManifestFile))
                prev[e.Url] = e;
            Console.WriteLine($"Incremental re-crawl: {prev.Count} page(s) from the previous manifest.");
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var manifest = new List<PageEntry>();

        // Seed from the TOC frame. Fall back to a couple of known entry points if it is unreachable.
        Console.WriteLine($"Fetching table of contents: {TocUrl}");
        FetchOutcome toc = await FetchAsync(TocUrl, null, ct).ConfigureAwait(false);
        visited.Add(Normalize(TocUrl)!);
        int seeded = 0;
        if (toc.Body is not null)
        {
            foreach (string link in await ExtractLinksAsync(toc.Body, TocUrl, ct).ConfigureAwait(false))
            {
                if (visited.Add(link)) { queue.Enqueue(link); seeded++; }
            }
        }
        if (seeded == 0)
        {
            foreach (string fallback in new[] { ContentPrefix + "welcome.htm", ContentPrefix + "title.htm" })
            {
                string n = Normalize(fallback)!;
                if (visited.Add(n)) queue.Enqueue(n);
            }
        }
        Console.WriteLine($"Seeded {queue.Count} page(s) from the TOC. Crawling (>= {_delayMs} ms/page)…");

        int total = 0, added = 0, changed = 0, unchanged = 0, failed = 0;
        bool first = true;
        while (queue.Count > 0 && total < _maxPages)
        {
            ct.ThrowIfCancellationRequested();
            string url = queue.Dequeue();
            prev.TryGetValue(url, out PageEntry? prior);

            // Courtesy throttle: wait between requests (not before the very first).
            if (!first) await Task.Delay(_delayMs, ct).ConfigureAwait(false);
            first = false;

            FetchOutcome res = await FetchAsync(url, prior, ct).ConfigureAwait(false);

            // 304 Not Modified: reuse the prior entry + saved HTML; still follow its links so
            // BFS discovery is unaffected by not re-downloading the page.
            if (res.Status == 304 && prior is not null)
            {
                manifest.Add(prior);
                unchanged++;
                total++;
                string savedPath = Path.Combine(_paths.HtmlDir, prior.RelPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(savedPath))
                {
                    string savedHtml = await File.ReadAllTextAsync(savedPath, ct).ConfigureAwait(false);
                    foreach (string link in await ExtractLinksAsync(savedHtml, url, ct).ConfigureAwait(false))
                        if (visited.Add(link)) queue.Enqueue(link);
                }
                Checkpoint(manifest, total);
                continue;
            }

            if (res.Body is null) { failed++; continue; } // 404 / gone / give-up

            string relPath = RelPathFor(url);
            string title = ExtractTitle(res.Body) ?? url;
            string hash = Sha256(res.Body);
            await SaveAsync(relPath, res.Body, ct).ConfigureAwait(false);
            manifest.Add(new PageEntry(url, relPath.Replace('\\', '/'), title, hash, res.ETag, res.LastModified));
            total++;

            string state = prior is null ? "new" : (prior.ContentHash == hash ? "unchanged" : "changed");
            if (prior is null) added++;
            else if (prior.ContentHash == hash) unchanged++;
            else changed++;
            Console.WriteLine($"  [{total}] {relPath}  ({title}) [{state}]");

            foreach (string link in await ExtractLinksAsync(res.Body, url, ct).ConfigureAwait(false))
            {
                if (visited.Add(link)) queue.Enqueue(link);
            }
            Checkpoint(manifest, total);
        }

        // Pages present last time but not seen now have been removed upstream.
        var seenUrls = new HashSet<string>(manifest.Select(m => m.Url), StringComparer.OrdinalIgnoreCase);
        int removed = prev.Keys.Count(u => !seenUrls.Contains(u));

        // Deterministic order makes the manifest diff-friendly across re-crawls.
        manifest.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        ManualManifest.Write(_paths.ManifestFile, manifest);

        Console.WriteLine();
        Console.WriteLine(
            $"Crawl complete: {total} page(s) — {added} new, {changed} changed, {unchanged} unchanged, " +
            $"{removed} removed, {failed} failed.");
        Console.WriteLine($"  HTML mirror : {_paths.HtmlDir}");
        Console.WriteLine($"  Manifest    : {_paths.ManifestFile}");
        return total;
    }

    /// <summary>Result of one fetch: HTTP status, body (null for 304/errors), and caching validators.</summary>
    private sealed record FetchOutcome(int Status, string? Body, string? ETag, string? LastModified);

    /// <summary>
    /// Conditional GET with a small retry budget. When <paramref name="prior"/> has an ETag /
    /// Last-Modified, they are replayed as If-None-Match / If-Modified-Since so an unchanged page
    /// returns 304 (cheap). Returns Status 304 with a null body on Not Modified; a null body with
    /// a 4xx/0 status on give-up.
    /// </summary>
    private async Task<FetchOutcome> FetchAsync(string url, PageEntry? prior, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(prior?.ETag))
                    request.Headers.TryAddWithoutValidation("If-None-Match", prior!.ETag);
                if (!string.IsNullOrEmpty(prior?.LastModified))
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", prior!.LastModified);

                using HttpResponseMessage res = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.NotModified)
                    return new FetchOutcome(304, null, prior?.ETag, prior?.LastModified);
                if (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.Gone)
                {
                    Console.WriteLine($"  ! {(int)res.StatusCode} {url}");
                    return new FetchOutcome((int)res.StatusCode, null, null, null);
                }
                if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new HttpRequestException($"HTTP {(int)res.StatusCode}");

                res.EnsureSuccessStatusCode();
                string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                string? etag = res.Headers.ETag?.Tag;
                string? lastMod = res.Content.Headers.LastModified?.ToString("R");
                return new FetchOutcome(200, body, etag, lastMod);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                Console.WriteLine($"  ~ retry {attempt}/2 for {url}: {ex.Message}");
                await Task.Delay(_delayMs * attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! give up on {url}: {ex.Message}");
                return new FetchOutcome(0, null, null, null);
            }
        }
        return new FetchOutcome(0, null, null, null);
    }

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>
    /// Persists the manifest every 25 pages so an interrupted crawl (crash, power loss) leaves a
    /// usable checkpoint on disk — the pages fetched so far can be converted/embedded, and a resumed
    /// crawl replays their ETags as conditional GETs. The final crawl writes the sorted manifest.
    /// </summary>
    private void Checkpoint(List<PageEntry> manifest, int total)
    {
        if (total % 25 == 0) ManualManifest.Write(_paths.ManifestFile, manifest);
    }

    private async Task<IReadOnlyList<string>> ExtractLinksAsync(string html, string baseUrl, CancellationToken ct)
    {
        var baseUri = new Uri(baseUrl);
        var links = new List<string>();
        var doc = await _parser.ParseDocumentAsync(html, ct).ConfigureAwait(false);
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            string? href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUri, href, out Uri? abs)) continue;

            string? norm = Normalize(abs.AbsoluteUri);
            if (norm is not null) links.Add(norm);
        }
        return links;
    }

    /// <summary>
    /// Canonicalizes a URL and accepts it only if it is a CLEAN <c>.htm/.html</c> page inside the
    /// manual's <c>html/</c> subtree. Strips the query/fragment. Returns null to reject.
    /// <para>Rejects crawler-trap URLs: resolving a page's <c>html/foo.htm</c>-style relative link
    /// against a page that already lives under <c>html/</c> synthesizes bogus nested paths
    /// (<c>…/html/html/…</c>) and double slashes (<c>…//…</c>) that the server may still 200 with
    /// DUPLICATE content — which then breed even deeper links. Every real page is still reachable
    /// via its clean sibling links, so filtering these loses no coverage.</para>
    /// </summary>
    private static string? Normalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return null;
        // Drop query + fragment; keep scheme/host/path only.
        string path = uri.GetLeftPart(UriPartial.Path);

        if (!path.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Allow the TOC frame itself (used as a visited-key), reject everything else.
            return path.Equals(TocUrl, StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        string lower = path.ToLowerInvariant();
        if (!lower.EndsWith(".htm") && !lower.EndsWith(".html")) return null;

        // Validate the tail under ".../html/": no empty segments (from '//'), no nested 'html/'
        // directory (the trap), and no absurd depth.
        string tail = path.Substring(ContentPrefix.Length);
        string[] segs = tail.Split('/');
        if (segs.Length > 6) return null;
        if (segs.Any(s => s.Length == 0)) return null;                       // '//' → empty segment
        // Check DIRECTORY segments only (exclude the filename) for a nested 'html'.
        for (int i = 0; i < segs.Length - 1; i++)
            if (segs[i].Equals("html", StringComparison.OrdinalIgnoreCase)) return null;

        return path;
    }

    /// <summary>
    /// Maps a content URL to its path relative to the HTML mirror root (URL-decoded), e.g.
    /// <c>plugins/MIDI Out.htm</c>. The leading <c>html/</c> is stripped because
    /// <see cref="ManualPaths.HtmlDir"/> IS that mirror root — otherwise pages would double-nest
    /// under <c>html/html/…</c>.
    /// </summary>
    private static string RelPathFor(string url)
    {
        string tail = url.Substring(SiteRoot.Length).TrimStart('/'); // "html/…"
        if (tail.StartsWith("html/", StringComparison.OrdinalIgnoreCase))
            tail = tail.Substring("html/".Length);
        tail = Uri.UnescapeDataString(tail);
        // Guard against path traversal / absolute rewrites from a malformed URL.
        string[] parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var safe = parts.Where(p => p is not "." and not "..").Select(SanitizeSegment);
        return Path.Combine(safe.ToArray());
    }

    private static string SanitizeSegment(string segment)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            segment = segment.Replace(c, '_');
        return segment;
    }

    private async Task SaveAsync(string relPath, string html, CancellationToken ct)
    {
        string full = Path.Combine(_paths.HtmlDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, html, ct).ConfigureAwait(false);
    }

    private static string? ExtractTitle(string html)
    {
        int i = html.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        int gt = html.IndexOf('>', i);
        int end = gt < 0 ? -1 : html.IndexOf("</title>", gt, StringComparison.OrdinalIgnoreCase);
        if (gt < 0 || end < 0) return null;
        string title = WebUtility.HtmlDecode(html.Substring(gt + 1, end - gt - 1)).Trim();
        return title.Length == 0 ? null : title;
    }
}
