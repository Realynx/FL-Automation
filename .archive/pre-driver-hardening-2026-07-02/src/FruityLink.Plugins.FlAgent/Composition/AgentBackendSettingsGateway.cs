using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm;
using FruityLink.Ui.Avalonia.Services;
using CoreAppSettings = FruityLink.Core.Configuration.AppSettings;

namespace FruityLink.Plugins.FlAgent.Composition;

/// <summary>
/// Bridges the backend-agnostic Avalonia Settings card (<see cref="IBackendSettingsGateway"/>) to the
/// AGENT's real configuration: reads/writes <see cref="AppSettings"/>/<see cref="LlmSettings"/> through
/// the SAME <see cref="ISettingsStore"/> (→ <c>settings.json</c>) the agent loads from, and the API key
/// through <see cref="ISecretStore"/> (→ encrypted <c>secrets.json</c>, DPAPI). After a save it calls
/// <see cref="FruityLink.Agent.FlAgent.ConfigureAsync"/> so the running agent picks up the new backend
/// without an FL restart. Mirrors the legacy WPF <c>SettingsViewModel</c>'s persistence + live-apply.
///
/// <para>The UI never sees the raw API key or any FruityLink.Core type — only the flat
/// <see cref="BackendSettings"/> DTO crosses the seam.</para>
/// </summary>
internal sealed class AgentBackendSettingsGateway : IBackendSettingsGateway
{
    // Same logical secret name the legacy app used, so an existing key keeps working across the migration.
    private const string ApiKeyRefName = "llm:apikey";

    // Shared, long-lived probe client with a short timeout so "Test connection" fails fast.
    private static readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly FruityLink.Agent.FlAgent _agent;
    private readonly ILlmConnectivity _connectivity;

    public AgentBackendSettingsGateway(
        ISettingsStore settings,
        ISecretStore secrets,
        FruityLink.Agent.FlAgent agent,
        ILlmConnectivity? connectivity = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _connectivity = connectivity ?? new OllamaOpenAiConnectivity(_probeHttp);
    }

    public IReadOnlyList<string> Providers { get; } = Enum.GetNames<LlmBackendKind>();

    public BackendSettings Load()
    {
        // Small local JSON read; the store uses ConfigureAwait(false) throughout so this can't deadlock
        // the UI thread. Kept synchronous to prefill the fields the instant the panel opens.
        CoreAppSettings appLoad = _settings.LoadAsync().GetAwaiter().GetResult();
        LlmSettings llm = appLoad.LlmOrDefault;
        return new BackendSettings
        {
            Provider = llm.Backend.ToString(),
            Endpoint = llm.Endpoint,
            Model = llm.Model,
            Deployment = llm.Deployment ?? string.Empty,
            HasApiKey = llm.ApiKeyRef is not null,
        };
    }

    public async Task SaveAsync(BackendSettings settings, string? newApiKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        CoreAppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
        LlmSettings existing = app.LlmOrDefault;

        // Preserve the stored key ref unless a new key was entered; a blank entry keeps the saved key.
        string? apiKeyRef = existing.ApiKeyRef;
        string? key = newApiKey?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            await _secrets.SetAsync(ApiKeyRefName, key, ct).ConfigureAwait(false);
            apiKeyRef = ApiKeyRefName;
        }

        LlmBackendKind backend = Enum.TryParse(settings.Provider, out LlmBackendKind parsed)
            ? parsed
            : LlmBackendKind.Ollama;

        var llm = new LlmSettings(
            backend,
            (settings.Endpoint ?? string.Empty).Trim(),
            (settings.Model ?? string.Empty).Trim(),
            apiKeyRef,
            string.IsNullOrWhiteSpace(settings.Deployment) ? null : settings.Deployment.Trim());

        // Persist FIRST (this is the durable part). Only the chat backend changes; embeddings/theme/etc.
        // on the existing AppSettings are preserved via the record `with`.
        CoreAppSettings updated = app with { Llm = llm };
        await _settings.SaveAsync(updated, ct).ConfigureAwait(false);

        // Then live-apply: rebuild the agent's kernel from the just-saved settings. If this throws, the
        // save has already landed on disk — the caller surfaces it as "saved, but couldn't configure".
        await _agent.ConfigureAsync(ct).ConfigureAwait(false);
    }

    public Task<bool> TestConnectionAsync(BackendSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LlmBackendKind backend = Enum.TryParse(settings.Provider, out LlmBackendKind parsed)
            ? parsed
            : LlmBackendKind.Ollama;
        var llm = new LlmSettings(backend, (settings.Endpoint ?? string.Empty).Trim(), (settings.Model ?? string.Empty).Trim());
        return _connectivity.IsReachableAsync(llm, ct);
    }
}
