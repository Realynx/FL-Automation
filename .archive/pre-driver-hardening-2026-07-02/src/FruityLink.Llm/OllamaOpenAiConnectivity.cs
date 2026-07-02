using System.Net.Http;
using FruityLink.Core.Configuration;

namespace FruityLink.Llm;

/// <summary>
/// <see cref="ILlmConnectivity"/> for OpenAI-compatible backends (Ollama, OpenAI, Azure-compatible
/// gateways). Probes <c>GET {endpoint}/models</c>, which every OpenAI-compatible server exposes and
/// which requires no request body. Always tolerant — any exception or non-success maps to <c>false</c>.
/// </summary>
public sealed class OllamaOpenAiConnectivity : ILlmConnectivity
{
    private readonly HttpClient _http;

    /// <summary>Creates a probe over the injected <see cref="HttpClient"/> (not disposed here).</summary>
    public OllamaOpenAiConnectivity(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <inheritdoc />
    public async Task<bool> IsReachableAsync(LlmSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            // Trim trailing slashes so the composed URL is always well-formed.
            string url = $"{settings.Endpoint.TrimEnd('/')}/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Honour genuine caller cancellation rather than swallowing it as "unreachable".
            throw;
        }
        catch
        {
            // Connection refused, DNS failure, timeout, TLS error, etc. -> simply not reachable.
            return false;
        }
    }
}
