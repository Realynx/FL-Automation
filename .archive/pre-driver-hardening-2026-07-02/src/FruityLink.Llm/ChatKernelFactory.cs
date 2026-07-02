using System.Net.Http;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Llm;

/// <summary>
/// Default <see cref="IChatKernelFactory"/>. Maps each <see cref="LlmBackendKind"/> onto a
/// Semantic Kernel chat-completion connector. No network call is performed during construction.
/// All backends share one diagnostic <see cref="HttpClient"/> that logs every attempt and retries
/// transient failures (see <see cref="LlmLoggingHandler"/> / <see cref="LlmRetryHandler"/>).
/// </summary>
public sealed class ChatKernelFactory : IChatKernelFactory
{
    /// <summary>
    /// Placeholder key used for Ollama when none is supplied. Ollama ignores the key, but several
    /// OpenAI-connector overloads reject a null/empty value, so a non-empty stand-in is needed.
    /// </summary>
    private const string OllamaPlaceholderKey = "ollama";

    private readonly HttpClient _httpClient;

    public ChatKernelFactory(ILlmDiagnostics diagnostics)
    {
        // Chain (outermost first): retry transient failures → log every attempt → repair malformed
        // tool-call argument JSON → the network. The agent runs non-streaming, so responses are a
        // single JSON body the repair handler can sanitize before Semantic Kernel parses them.
        // Decompress at the transport so the repair handler always sees plaintext JSON — a gateway
        // (Cloudflare/nginx) in front of a local LLM may gzip unconditionally, and reading a gzipped
        // body as a string yields garbage the repair pass can't parse.
        var transport = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        var handler = new LlmRetryHandler(
            new LlmLoggingHandler(
                new LlmToolCallRepairHandler(transport),
                diagnostics));
        _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <inheritdoc />
    public Kernel CreateKernel(LlmSettings settings, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IKernelBuilder builder = Kernel.CreateBuilder();

        switch (settings.Backend)
        {
            case LlmBackendKind.AzureOpenAI:
                // Verified against Connectors.AzureOpenAI 1.77.0:
                //   AddAzureOpenAIChatCompletion(string deploymentName, string endpoint, string apiKey,
                //                                string? serviceId = null, string? modelId = null,
                //                                HttpClient? httpClient = null, string? apiVersion = null)
                // 'endpoint' is a string here (not a Uri).
                builder.AddAzureOpenAIChatCompletion(
                    deploymentName: settings.Deployment ?? settings.Model,
                    endpoint: settings.Endpoint,
                    apiKey: apiKey ?? string.Empty,
                    httpClient: _httpClient);
                break;

            case LlmBackendKind.Ollama:
            case LlmBackendKind.OpenAI:
            case LlmBackendKind.Anthropic:
            default:
                // Ollama, OpenAI, and an Anthropic-as-OpenAI-proxy all speak the OpenAI chat API,
                // so they share the OpenAI connector pointed at the configured endpoint.
                //
                // Verified against Connectors.OpenAI 1.77.0 — a DIRECT custom-endpoint overload exists:
                //   AddOpenAIChatCompletion(string modelId, Uri endpoint, string? apiKey = null,
                //                           string? orgId = null, string? serviceId = null,
                //                           HttpClient? httpClient = null)
                // So the OpenAI.OpenAIClient + OpenAIClientOptions.Endpoint fallback is NOT needed.
                string key = settings.Backend == LlmBackendKind.Ollama
                    ? apiKey ?? OllamaPlaceholderKey
                    : apiKey ?? string.Empty;

                builder.AddOpenAIChatCompletion(
                    modelId: settings.Model,
                    endpoint: new Uri(settings.Endpoint),
                    apiKey: key,
                    httpClient: _httpClient);
                break;
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public IChatCompletionService CreateChatService(LlmSettings settings, string? apiKey = null)
        => CreateKernel(settings, apiKey).GetRequiredService<IChatCompletionService>();
}
