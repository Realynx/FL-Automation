using System;
using System.Text.Json.Serialization;

namespace FruityLink.Core.Configuration;

/// <summary>
/// The FL Automate ACCOUNT connection: the plugin signs into the marketing API with the user's
/// email/password and drives the LLM through our OpenAI-compatible AI gateway with a Bearer JWT.
/// There is no user-configurable third-party backend any more — only these bases (dev-only
/// hand-editable settings.json overrides; the Settings UI neither shows nor changes them) and the
/// model choice.
/// </summary>
/// <param name="ApiBaseUrl">Marketing/auth API base (login / refresh / logout / me).</param>
/// <param name="GatewayBaseUrl">AI gateway base; chat goes to <c>{GatewayBaseUrl}/v1/chat/completions</c>.</param>
/// <param name="Model">Model id sent to the gateway. <c>"default"</c> lets the gateway pick the
/// plan's default model.</param>
public sealed record AccountSettings(
    string ApiBaseUrl = AccountSettings.DefaultApiBaseUrl,
    string GatewayBaseUrl = AccountSettings.DefaultGatewayBaseUrl,
    string Model = AccountSettings.DefaultModel)
{
    /// <summary>Default marketing API base (overridable in settings.json for dev/staging).</summary>
    public const string DefaultApiBaseUrl = "https://fl-automate.com/api";

    /// <summary>Default AI gateway base (overridable in settings.json for dev/staging).</summary>
    public const string DefaultGatewayBaseUrl = "https://ai.fl-automate.com";

    /// <summary>The sentinel model id the gateway maps onto the plan's default model.</summary>
    public const string DefaultModel = "default";

    /// <summary>
    /// Whether the agent may request MULTIPLE tool calls in one model response (fewer round-trips).
    /// Default true. A manual settings.json escape hatch — kept OUT of the positional parameter list
    /// so every existing construction site compiles unchanged: some gateway-fronted models
    /// mis-serialize parallel tool_calls into unparseable JSON; set false to force one call per
    /// response for those. Invocation is always sequential either way — this only shapes the
    /// model's RESPONSE format.
    /// </summary>
    public bool AllowParallelToolCalls { get; init; } = true;

    /// <summary>
    /// Whether the agent STREAMS the model's reply tokens as they arrive (live text in the chat
    /// bubble) instead of buffering the whole completion. Default true. A manual settings.json
    /// escape hatch — kept OUT of the positional parameter list so every existing construction
    /// site compiles unchanged. Streamed (text/event-stream) responses bypass the buffered-body
    /// wire repair (<c>LlmToolCallRepairHandler</c> hard-skips SSE), so a backend whose streamed
    /// tool-call arguments arrive corrupted can be forced back onto the fully-repairable
    /// non-streaming path by setting this false. The turn runner ALSO falls back to one
    /// non-streaming retry automatically when a streamed turn dies on a JSON-shaped error.
    /// </summary>
    public bool StreamResponses { get; init; } = true;

    /// <summary>
    /// Max auto-invoke tool rounds (model round-trips, each possibly batching several tool calls)
    /// a single turn may run before the agent stops and asks the user to say "continue". Default
    /// 40 — big jobs (chop + arrange + route a whole session) legitimately chain dozens of tool
    /// rounds; the cap only exists to bound a RUNAWAY loop, not honest work. A manual
    /// settings.json escape hatch — kept OUT of the positional parameter list so every existing
    /// construction site compiles unchanged. Clamped to [1, 200] at the use site.
    /// </summary>
    public int MaxToolRoundsPerTurn { get; init; } = 40;

    /// <summary>Signed-in account email, persisted for DISPLAY only (the tokens live in the
    /// encrypted secret store). Null when no one has signed in on this machine.</summary>
    public string? Email { get; init; }

    /// <summary>Signed-in account plan ('free' | 'beta' | 'pro' | 'studio'), for display only.</summary>
    public string? Plan { get; init; }

    /// <summary>The gateway's OpenAI-compatible root (<c>{GatewayBaseUrl}/v1</c>).</summary>
    [JsonIgnore]
    public string GatewayOpenAiBase => GatewayBaseUrl.TrimEnd('/') + "/v1";

    /// <summary>The model id to actually send: a blank <see cref="Model"/> falls back to
    /// <see cref="DefaultModel"/> so the gateway picks the plan's default.</summary>
    [JsonIgnore]
    public string ModelOrDefault => string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim();

    /// <summary>The marketing site's account-management page, derived from <see cref="ApiBaseUrl"/>
    /// (the API lives at <c>{site}/api</c>): strip the trailing <c>/api</c>, append
    /// <c>/account</c>. Signed-out visitors get the site's login/register flow first.</summary>
    [JsonIgnore]
    public string AccountPageUrl
    {
        get
        {
            string site = ApiBaseUrl.TrimEnd('/');
            if (site.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
                site = site[..^"/api".Length];
            return site + "/account";
        }
    }
}
