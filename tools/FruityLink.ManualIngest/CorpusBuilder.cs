using System.Net;
using FruityLink.Core.Configuration;
using FruityLink.Knowledge;
using FruityLink.Llm;

namespace FruityLink.ManualIngest;

/// <summary>
/// Phase 4 — embeds the converted markdown into the shipped SQLite vector DB using the same
/// <see cref="KnowledgeService"/> the plugin queries at runtime, so the on-disk schema and vector
/// encoding are identical. Embeddings are generated once here (offline) with Azure OpenAI
/// <c>text-embedding-3-large</c>; at runtime the plugin embeds the query with the SAME model
/// (via the gateway) so cosine search is comparable.
/// </summary>
public sealed class CorpusBuilder
{
    private readonly ManualPaths _paths;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly string _apiKey;
    private readonly bool _fresh;

    public CorpusBuilder(
        ManualPaths paths, string endpoint, string model, int dimensions, string apiKey, bool fresh = false)
    {
        _paths = paths;
        _endpoint = endpoint;
        _model = model;
        _dimensions = dimensions;
        _apiKey = apiKey;
        _fresh = fresh;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<MarkdownEntry> pages = ManualManifest.Read<MarkdownEntry>(_paths.MarkdownIndexFile);
        if (pages.Count == 0)
        {
            Console.Error.WriteLine("No markdown pages found. Run 'convert' first.");
            return 0;
        }

        // Incremental by default: keep the existing DB so unchanged pages are skipped and a long
        // first build is resumable after a failure. --fresh forces a clean rebuild.
        if (_fresh && File.Exists(_paths.DbFile)) File.Delete(_paths.DbFile);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.DbFile)!);

        // ConnectTimeout guards against a dead/half-open socket hanging forever (defaults to
        // infinite on SocketsHttpHandler); the request Timeout bounds a slow response. A stalled
        // page then fails fast, is counted, and the incremental resume re-embeds just it.
        using var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        var settings = new EmbeddingSettings(Endpoint: _endpoint, Model: _model, Dimensions: _dimensions);
        var embeddings = new OpenAiCompatibleEmbeddingClient(http, settings, _apiKey);
        var knowledge = new KnowledgeService(embeddings, _paths.DbFile, http);

        Console.WriteLine(
            $"Embedding {pages.Count} page(s) with '{_model}' via {_endpoint} " +
            $"({(_fresh ? "fresh rebuild" : "incremental — unchanged pages skipped")})…");

        int done = 0, embedded = 0, reused = 0, chunks = 0, failed = 0;
        foreach (MarkdownEntry page in pages)
        {
            ct.ThrowIfCancellationRequested();
            string mdFile = Path.Combine(_paths.MarkdownDir, page.MdPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(mdFile)) { Console.WriteLine($"  ! missing {page.MdPath}"); continue; }

            string text = await File.ReadAllTextAsync(mdFile, ct).ConfigureAwait(false);
            try
            {
                TextIngestResult r = await knowledge.AddTextSourceAsync(page.Url, page.Title, text, ct)
                    .ConfigureAwait(false);
                chunks += r.ChunkCount;
                if (r.Reused) reused++; else embedded++;
                done++;
                if (done % 25 == 0 || done == pages.Count)
                    Console.WriteLine($"  {done}/{pages.Count} pages ({embedded} embedded, {reused} reused), {chunks} chunks…");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  ! embed failed for {page.Title}: {ex.Message}");
                if (failed >= 5)
                {
                    Console.Error.WriteLine("Too many embedding failures — aborting. Check the endpoint/key/model.");
                    throw;
                }
            }
        }

        // Drop any indexed page that no longer exists in the current markdown set.
        int pruned = await knowledge.PruneSourcesNotInAsync(pages.Select(p => p.Url), ct).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(
            $"Build complete: {embedded} embedded, {reused} reused, {pruned} pruned; " +
            $"{chunks} chunk(s) total ({_dimensions}-dim), {failed} failed.");
        Console.WriteLine($"  Vector DB : {_paths.DbFile}");
        return chunks;
    }
}
