using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;

namespace FruityLink.Llm.Diagnostics;

/// <summary>Details of one saved-model fallback, for the composition's self-heal hook.</summary>
/// <param name="StaleModel">The rejected model id the request carried.</param>
/// <param name="AllowedModels">The plan's menu as reported by the gateway (may be empty).</param>
public sealed record LlmModelFallback(string StaleModel, IReadOnlyList<string> AllowedModels);

/// <summary>
/// Recovers a chat turn whose SAVED model the gateway no longer offers. The account's plan decides
/// the model menu, so a plan change (admin edit, subscription change, menu re-configured in the Ops
/// Console) can strand the plugin's persisted model choice — the gateway then 400s EVERY turn with
/// <c>"Model 'X' is not allowed. Allowed models: …"</c> and the chat is bricked until the user
/// re-picks a model by hand.
/// <para>
/// On that exact 400 this handler resends the request ONCE with <c>model: "default"</c> (the
/// gateway maps it to the plan's default menu entry) and reports the stale model through
/// <c>onFallback</c> (once per stale id per session) so the composition can persist the healed
/// choice and rebuild kernels. Any other 400 — or a second rejection of the fallback itself —
/// passes through untouched (body re-buffered, so downstream error handling still reads it).
/// </para>
/// </summary>
public sealed class LlmModelFallbackHandler : DelegatingHandler
{
    /// <summary>Matches the gateway's model-not-allowed message (resolveModelForPlan's 400).</summary>
    private static readonly Regex NotAllowed = new(
        @"^Model '(?<model>.+)' is not allowed\. Allowed models: (?<allowed>.+)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly ILlmDiagnostics _diagnostics;
    private readonly Action<LlmModelFallback>? _onFallback;
    private readonly HashSet<string> _reportedModels = new(StringComparer.Ordinal);
    private readonly object _reportedLock = new();

    public LlmModelFallbackHandler(
        HttpMessageHandler innerHandler,
        ILlmDiagnostics diagnostics,
        Action<LlmModelFallback>? onFallback = null)
        : base(innerHandler)
    {
        _diagnostics = diagnostics;
        _onFallback = onFallback;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body up front (like the auth handler's 401 retry): the resend needs it, and
        // an HttpRequestMessage can only be sent once.
        byte[]? body = request.Content is null
            ? null
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.BadRequest
            || !LlmHttp.IsChatCompletions(request)
            || body is null)
        {
            return response;
        }

        // A 400 is never a live SSE stream, so reading the body is safe — but re-buffer it so a
        // pass-through still hands downstream a readable error.
        string errorBody;
        try { errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch { return response; }
        HttpContentRebuffer.Replace(response, errorBody);

        Match match = MatchNotAllowed(errorBody);
        if (!match.Success) return response;

        // Never loop: only fall back when the request named a REAL model — a rejected
        // "default"/"auto" means the plan's menu itself is the problem, not a stale choice.
        string? requestedModel = ReadRequestModel(body);
        if (requestedModel is null
            || string.Equals(requestedModel, AccountSettings.DefaultModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestedModel, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        byte[]? fallbackBody = RewriteModel(body, AccountSettings.DefaultModel);
        if (fallbackBody is null) return response; // unparseable request body — leave the 400 as-is

        string[] allowed = match.Groups["allowed"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Report(request, requestedModel, allowed);

        response.Dispose();
        using HttpRequestMessage retry = HttpRequestCloner.Clone(request, fallbackBody);
        retry.Content!.Headers.ContentLength = fallbackBody.Length; // the clone copied the ORIGINAL length
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses the gateway error body and matches its <c>message</c> against the
    /// model-not-allowed shape. Anything unparseable simply doesn't match.</summary>
    private static Match MatchNotAllowed(string errorBody)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(errorBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return NotAllowed.Match(message.GetString() ?? string.Empty);
            }
        }
        catch (JsonException) { /* not JSON -> no match */ }
        return Match.Empty;
    }

    private static string? ReadRequestModel(byte[] body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("model", out JsonElement model)
                && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Re-serializes the request body with <c>model</c> replaced, or null when the body
    /// isn't a JSON object (nothing safe to rewrite).</summary>
    private static byte[]? RewriteModel(byte[] body, string model)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root) return null;
            root["model"] = model;
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>One diagnostics entry per fallback; the self-heal hook fires once per stale model
    /// per session (every in-flight request hits the same stale id until the heal lands).</summary>
    private void Report(HttpRequestMessage request, string staleModel, string[] allowed)
    {
        _diagnostics.Record(new LlmCallRecord
        {
            Timestamp = DateTimeOffset.Now,
            Method = "FALLBACK",
            Uri = request.RequestUri?.ToString() ?? string.Empty,
            StatusCode = 400,
            Ok = true,
            Attempt = request.Options.TryGetValue(LlmLoggingHandler.AttemptKey, out int a) ? a : 1,
            ResponsePreview =
                $"model '{staleModel}' no longer offered by the plan (menu: {string.Join(", ", allowed)}) " +
                "— resent as 'default'",
        });

        if (_onFallback is null) return;
        lock (_reportedLock)
        {
            if (!_reportedModels.Add(staleModel)) return;
        }
        try { _onFallback(new LlmModelFallback(staleModel, allowed)); }
        catch { /* the heal hook must never break the request path */ }
    }
}
