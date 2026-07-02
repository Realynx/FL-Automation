using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FruityLink.Ui.Avalonia.Services;

/// <summary>
/// A backend-agnostic snapshot of the agent's LLM connection settings. Deliberately a plain POCO of
/// primitives so the Avalonia UI lib stays free of any reference to <c>FruityLink.Core</c> /
/// <c>FruityLink.Persistence</c> — the FL Agent plugin maps this to/from the real
/// <c>AppSettings</c>/<c>LlmSettings</c> when it implements <see cref="IBackendSettingsGateway"/>.
///
/// <para>The API key itself is never carried on this DTO (it lives in the encrypted secret store);
/// <see cref="HasApiKey"/> only records whether one is already set, so the UI can show a
/// "•••• set" indicator instead of the raw value.</para>
/// </summary>
public sealed class BackendSettings
{
    /// <summary>Provider name, one of <see cref="IBackendSettingsGateway.Providers"/> (e.g. "Ollama", "OpenAI").</summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>Base URL of the backend (e.g. http://localhost:11434/v1 for Ollama).</summary>
    public string Endpoint { get; set; } = "http://localhost:11434/v1";

    /// <summary>Model id (must support tool calling, e.g. "qwen2.5", "gpt-4o").</summary>
    public string Model { get; set; } = "qwen2.5";

    /// <summary>Azure OpenAI deployment name (Azure only; empty otherwise).</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>True when an API key is already stored (so the UI shows "set" and a blank entry keeps it).</summary>
    public bool HasApiKey { get; set; }
}

/// <summary>
/// The seam the Avalonia Settings view-model depends on to read/write the AGENT's real backend
/// configuration WITHOUT the UI lib referencing the persistence/core layers. The FL Agent plugin
/// implements this against the agent's <c>ISettingsStore</c> (→ settings.json) and <c>ISecretStore</c>
/// (→ encrypted secrets.json), and re-configures the live agent after a save. The standalone dev head
/// uses <see cref="InMemoryBackendSettingsGateway"/> so <c>dotnet run</c> still works with no backend.
/// </summary>
public interface IBackendSettingsGateway
{
    /// <summary>The selectable provider names, in a stable order (mirrors the agent's backend enum).</summary>
    IReadOnlyList<string> Providers { get; }

    /// <summary>Loads the current persisted backend settings (never null).</summary>
    BackendSettings Load();

    /// <summary>
    /// Persists <paramref name="settings"/> to the agent's settings store, and — when
    /// <paramref name="newApiKey"/> is non-blank — writes it to the encrypted secret store (a blank
    /// value keeps the existing key). After persisting, re-configures the live agent so the change takes
    /// effect without an FL restart. May throw if the agent re-configure fails; the on-disk save has
    /// already happened by then.
    /// </summary>
    Task SaveAsync(BackendSettings settings, string? newApiKey, CancellationToken ct = default);

    /// <summary>Probes reachability for the (possibly unsaved) <paramref name="settings"/>. Never throws for
    /// connection errors — returns false. Only genuine caller cancellation propagates.</summary>
    Task<bool> TestConnectionAsync(BackendSettings settings, CancellationToken ct = default);
}

/// <summary>
/// No-backend gateway for the standalone Avalonia dev head (<c>dotnet run</c>): keeps edits in memory
/// for the process lifetime, never touches disk, and reports the backend as unreachable. Lets the
/// Settings UI stay fully interactive with no agent wired.
/// </summary>
public sealed class InMemoryBackendSettingsGateway : IBackendSettingsGateway
{
    private BackendSettings _current = new();

    public IReadOnlyList<string> Providers { get; } = new[] { "Ollama", "OpenAI", "AzureOpenAI", "Anthropic" };

    public BackendSettings Load() => new()
    {
        Provider = _current.Provider,
        Endpoint = _current.Endpoint,
        Model = _current.Model,
        Deployment = _current.Deployment,
        HasApiKey = _current.HasApiKey,
    };

    public Task SaveAsync(BackendSettings settings, string? newApiKey, CancellationToken ct = default)
    {
        _current = new BackendSettings
        {
            Provider = settings.Provider,
            Endpoint = settings.Endpoint,
            Model = settings.Model,
            Deployment = settings.Deployment,
            HasApiKey = settings.HasApiKey || !string.IsNullOrWhiteSpace(newApiKey),
        };
        return Task.CompletedTask;
    }

    public Task<bool> TestConnectionAsync(BackendSettings settings, CancellationToken ct = default)
        => Task.FromResult(false);
}
