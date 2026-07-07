using System.Net;
using FruityLink.Core.Configuration;
using FruityLink.Core.Domain;
using FruityLink.Knowledge;
using FruityLink.Llm;

namespace FruityLink.ManualIngest;

/// <summary>
/// Offline verification / smoke-test for the built corpus: embeds a query with the SAME Azure model
/// the corpus was built with (bypassing the gateway, which is unit-tested separately) and runs the
/// real <see cref="KnowledgeService"/> cosine search against <c>fl-manual.db</c>, printing the top
/// hits. Confirms the DB is populated and retrieval returns sensible, on-topic manual pages.
/// </summary>
public sealed class SearchRunner
{
    private readonly ManualPaths _paths;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly int _dimensions;
    private readonly string _apiKey;

    public SearchRunner(ManualPaths paths, string endpoint, string model, int dimensions, string apiKey)
    {
        _paths = paths;
        _endpoint = endpoint;
        _model = model;
        _dimensions = dimensions;
        _apiKey = apiKey;
    }

    public async Task<int> RunAsync(string query, int topK, CancellationToken ct)
    {
        if (!File.Exists(_paths.DbFile))
        {
            Console.Error.WriteLine($"No vector DB at {_paths.DbFile}. Run 'build' first.");
            return 1;
        }

        using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(1),
        };
        var settings = new EmbeddingSettings(Endpoint: _endpoint, Model: _model, Dimensions: _dimensions);
        var embeddings = new OpenAiCompatibleEmbeddingClient(http, settings, _apiKey);
        var knowledge = new KnowledgeService(embeddings, _paths.DbFile, http);

        Console.WriteLine($"Query: \"{query}\"  (top {topK})\n");
        IReadOnlyList<KnowledgeHit> hits = await knowledge.SearchAsync(query, topK, ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            Console.WriteLine("No hits.");
            return 0;
        }

        foreach ((KnowledgeHit hit, int i) in hits.Select((h, i) => (h, i)))
        {
            string snippet = hit.Text.Length > 240 ? hit.Text[..240].ReplaceLineEndings(" ") + "…" : hit.Text.ReplaceLineEndings(" ");
            Console.WriteLine($"[{i + 1}] score {hit.Score:0.000}  {hit.SourceTitle}");
            Console.WriteLine($"     {snippet}\n");
        }
        return 0;
    }
}
