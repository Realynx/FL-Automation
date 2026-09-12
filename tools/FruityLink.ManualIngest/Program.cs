using FruityLink.ManualIngest;

// ---------------------------------------------------------------------------
// FL Studio manual → RAG corpus builder (offline data-prep).
//
//   manualingest crawl   [--root DIR] [--delay-ms 1000] [--max N]
//   manualingest convert [--root DIR]
//   manualingest build   [--root DIR] [--endpoint URL] [--model M]
//                        [--dimensions 3072] [--key KEY] [--db PATH]
//   manualingest all     (crawl → convert → build)
//
// Defaults target Azure OpenAI text-embedding-3-large. The embedding key is
// read from --key or the MANUAL_EMBED_KEY / AZURE_OPENAI_API_KEY env var.
// ---------------------------------------------------------------------------

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

string command = args[0].ToLowerInvariant();
var opts = ParseFlags(args.Skip(1));

string root = opts.GetValueOrDefault("root", "FL Studio online manual");
var paths = new ManualPaths(root);

// Ctrl+C cancels cleanly (partial progress on disk is still valid to resume from).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

const string DefaultEndpoint = "https://obsidianfox-ai.services.ai.azure.com/openai/v1";
const string DefaultModel = "text-embedding-3-large";
const int DefaultDimensions = 3072;

try
{
    switch (command)
    {
        case "crawl":
            await CrawlAsync();
            break;

        case "convert":
            await ConvertAsync();
            break;

        case "build":
            await BuildAsync(ResolveKey(opts));
            break;

        case "search":
        {
            string key = ResolveKey(opts);
            string query = opts.GetValueOrDefault("query", "").Trim();
            if (query.Length == 0) { Console.Error.WriteLine("Provide --query \"...\"."); return 2; }
            (string endpoint, string model, int dimensions) = EmbeddingOptions();
            await new SearchRunner(paths, endpoint, model, dimensions, key)
                .RunAsync(query, Int(opts, "topk", 5), cts.Token);
            break;
        }

        case "all":
        {
            // Resolve the key up front so a missing key fails fast instead of after a long crawl.
            string key = ResolveKey(opts);
            await CrawlAsync();
            await ConvertAsync();
            await BuildAsync(key);
            break;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintUsage();
            return 2;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

return 0;

// --- helpers ---------------------------------------------------------------

Task CrawlAsync()
    => new Crawler(paths, Int(opts, "delay-ms", 1000), Int(opts, "max", 0)).RunAsync(cts.Token);

Task ConvertAsync()
    => new HtmlToMarkdown(paths).RunAsync(cts.Token);

Task BuildAsync(string key)
{
    (string endpoint, string model, int dimensions) = EmbeddingOptions();
    return new CorpusBuilder(paths, endpoint, model, dimensions, key, fresh: opts.ContainsKey("fresh"))
        .RunAsync(cts.Token);
}

(string Endpoint, string Model, int Dimensions) EmbeddingOptions() => (
    opts.GetValueOrDefault("endpoint", DefaultEndpoint),
    opts.GetValueOrDefault("model", DefaultModel),
    Int(opts, "dimensions", DefaultDimensions));

static Dictionary<string, string> ParseFlags(IEnumerable<string> args)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? pending = null;
    foreach (string a in args)
    {
        if (a.StartsWith("--", StringComparison.Ordinal))
        {
            if (pending is not null) map[pending] = "true"; // bare flag
            string body = a[2..];
            int eq = body.IndexOf('=');
            if (eq >= 0) { map[body[..eq]] = body[(eq + 1)..]; pending = null; }
            else pending = body;
        }
        else if (pending is not null) { map[pending] = a; pending = null; }
    }
    if (pending is not null) map[pending] = "true";
    return map;
}

static int Int(Dictionary<string, string> opts, string key, int fallback)
    => opts.TryGetValue(key, out string? v) && int.TryParse(v, out int n) ? n : fallback;

static string ResolveKey(Dictionary<string, string> opts)
{
    string key = opts.GetValueOrDefault("key", "").Trim();
    if (key.Length == 0)
        key = (Environment.GetEnvironmentVariable("MANUAL_EMBED_KEY")
               ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
               ?? "").Trim();
    if (key.Length == 0)
    {
        throw new InvalidOperationException(
            "No embedding API key. Pass --key <KEY> or set MANUAL_EMBED_KEY / AZURE_OPENAI_API_KEY.");
    }
    return key;
}

static void PrintUsage()
{
    Console.WriteLine(
        """
        FL Studio manual → RAG corpus builder

          manualingest crawl   [--root DIR] [--delay-ms 1000] [--max N]
          manualingest convert [--root DIR]
          manualingest build   [--root DIR] [--endpoint URL] [--model M]
                               [--dimensions 3072] [--key KEY] [--fresh]
          manualingest search  --query "..." [--topk 5] [--key KEY]
          manualingest all     [ ...all of the above flags... ]

        Defaults: Azure OpenAI text-embedding-3-large (3072-dim).
        Key: --key, or MANUAL_EMBED_KEY / AZURE_OPENAI_API_KEY env var.
        Root defaults to "FL Studio online manual".
        """);
}
