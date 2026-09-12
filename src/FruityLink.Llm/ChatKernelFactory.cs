using System.Net.Http;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Auth;
using FruityLink.Llm.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Llm;

/// <summary>
/// Default <see cref="IChatKernelFactory"/>: one path, the FL Automate AI gateway (an
/// OpenAI-compatible endpoint at <c>{gateway}/v1</c>) reached through the Semantic Kernel OpenAI
/// connector. Authentication is a Bearer JWT stamped per-request by <see cref="GatewayAuthHandler"/>
/// (the connector's static API key is a placeholder the handler overwrites). No network call is
/// performed during construction. All kernels share one diagnostic <see cref="HttpClient"/> that
/// logs every attempt and retries transient failures.
/// </summary>
public sealed class ChatKernelFactory : IChatKernelFactory
{
    /// <summary>
    /// Placeholder key handed to the OpenAI connector (it rejects a null/empty key). Never reaches
    /// the wire: <see cref="GatewayAuthHandler"/> replaces the Authorization header on every request
    /// with the account's live access token.
    /// </summary>
    private const string PlaceholderKey = "fl-automate";

    private readonly HttpClient _httpClient;

    public ChatKernelFactory(
        ILlmDiagnostics diagnostics,
        IAccountAuth auth,
        Action<LlmModelFallback>? onModelFallback = null)
    {
        ArgumentNullException.ThrowIfNull(auth);

        // Chain (outermost first): gateway auth (stamp the Bearer token; one refresh+retry on 401;
        // map 402/403 quota/subscription statuses onto readable errors — outermost so its retry
        // re-enters the whole pipeline as an ordinary request) → fall back to the plan-default
        // model when the gateway rejects a stale saved model (a plan change would otherwise brick
        // every turn with 400; inside auth so the resend reuses the stamped token, outside the
        // budget so the resend is counted) → enforce the per-turn request budget (a runaway
        // auto-invoke loop whose rounds all fail tool validation bypasses the round-cap filter —
        // the budget is the layer that sees EVERY round) → retry transient failures → log every
        // attempt → repair malformed tool-call argument JSON → the network.
        // The agent runs non-streaming, so responses are a single JSON body the repair handler can
        // sanitize before Semantic Kernel parses them.
        // Decompress at the transport so the repair handler always sees plaintext JSON — a gateway
        // (Cloudflare/nginx) may gzip unconditionally, and reading a gzipped body as a string
        // yields garbage the repair pass can't parse.
        var transport = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
        // The inner handlers receive the diagnostics sink: the logger records every attempt (and
        // usage telemetry on success), the repair and fallback handlers record what they had to
        // fix — repairs, fallbacks, and their failures must never be silent.
        var handler = new GatewayAuthHandler(auth,
            new LlmModelFallbackHandler(
                new LlmTurnBudgetHandler(
                    new LlmRetryHandler(
                        new LlmLoggingHandler(
                            new LlmToolCallRepairHandler(transport, diagnostics),
                            diagnostics)),
                    diagnostics),
                diagnostics,
                onModelFallback));
        _httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <inheritdoc />
    public Kernel CreateKernel(AccountSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IKernelBuilder builder = Kernel.CreateBuilder();

        // Verified against Connectors.OpenAI 1.77.0 — a DIRECT custom-endpoint overload exists:
        //   AddOpenAIChatCompletion(string modelId, Uri endpoint, string? apiKey = null,
        //                           string? orgId = null, string? serviceId = null,
        //                           HttpClient? httpClient = null)
        // "default" as the model id lets the gateway map to the plan's default model.
        builder.AddOpenAIChatCompletion(
            modelId: settings.ModelOrDefault,
            endpoint: new Uri(settings.GatewayOpenAiBase),
            apiKey: PlaceholderKey,
            httpClient: _httpClient);

        return builder.Build();
    }

    /// <inheritdoc />
    public IChatCompletionService CreateChatService(AccountSettings settings)
        => CreateKernel(settings).GetRequiredService<IChatCompletionService>();
}
