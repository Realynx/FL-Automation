using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;

namespace FruityLink.Llm;

/// <summary>
/// <see cref="IEmbeddingClient"/> backed by an OpenAI-compatible <c>/embeddings</c> endpoint.
/// Works for Ollama (<c>http://localhost:11434/v1</c> + <c>nomic-embed-text</c>), OpenAI, and
/// Azure-compatible gateways. The <see cref="HttpClient"/> is injected (never constructed here)
/// so callers control its lifetime/handler and tests can supply a fake handler.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly string _embeddingsUrl;
    private readonly string _model;
    private readonly string? _apiKey;

    /// <summary>
    /// Creates a client for the embeddings connection.
    /// </summary>
    /// <param name="http">Injected HTTP client. Not disposed by this type.</param>
    /// <param name="settings">Embeddings connection (endpoint + model). Standalone from chat: chat
    /// goes through the FL Automate gateway, embeddings stay a local OpenAI-compatible endpoint.</param>
    /// <param name="apiKey">
    /// Optional API key. When non-empty a <c>Bearer</c> <c>Authorization</c> header is sent;
    /// Ollama ignores it, so a local daemon needs no key.
    /// </param>
    public OpenAiCompatibleEmbeddingClient(HttpClient http, EmbeddingSettings settings, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);

        _http = http;
        _model = settings.Model;
        // Trim trailing slashes so "<endpoint>/" and "<endpoint>" both compose one clean URL.
        _embeddingsUrl = $"{settings.Endpoint.TrimEnd('/')}/embeddings";
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    }

    /// <inheritdoc />
    public async Task<float[]> EmbedAsync(string input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<float[]> batch = await EmbedAsync(new[] { input }, ct).ConfigureAwait(false);
        return batch[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var request = new EmbeddingRequest(_model, inputs);

        // Build a per-request message so the Authorization header is never written to the shared
        // HttpClient's DefaultRequestHeaders (which would race across callers and leak stale keys).
        // Deliberately NOT System.Net.Http.Json (JsonContent/ReadFromJsonAsync): in the FL plugin
        // ALC the local System.Text.Json (10.x) and the host framework's System.Net.Http.Json (9.x)
        // have split type identities → MissingMethodException. Serialize by hand instead.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _embeddingsUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, LlmJson.Web), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null)
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using HttpResponseMessage response = await _http
            .SendAsync(httpRequest, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string snippet = body.Length > 512 ? body[..512] + "…" : body;
            throw new HttpRequestException(
                $"Embeddings request to '{_embeddingsUrl}' failed with status {(int)response.StatusCode} " +
                $"({response.ReasonPhrase}). Body: {snippet}");
        }

        string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        EmbeddingResponse? parsed = JsonSerializer.Deserialize<EmbeddingResponse>(payload, LlmJson.Web);

        if (parsed?.Data is null)
        {
            throw new HttpRequestException(
                $"Embeddings response from '{_embeddingsUrl}' contained no 'data' array.");
        }

        // The endpoint preserves input order; map straight through to keep that guarantee.
        var vectors = new float[parsed.Data.Count][];
        for (int i = 0; i < parsed.Data.Count; i++)
        {
            vectors[i] = parsed.Data[i].Embedding ?? Array.Empty<float>();
        }

        return vectors;
    }

    /// <summary>Request body: <c>{"model":..,"input":[..]}</c>.</summary>
    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    /// <summary>Response body: <c>{"data":[{"embedding":[..]}, ..]}</c>.</summary>
    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<EmbeddingDatum> Data);

    private sealed record EmbeddingDatum(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
