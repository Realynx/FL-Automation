using System.Text.Json;

namespace FruityLink.Plugins.PythonIde.Execution;

/// <summary>IDE adapter over the shared framework interpreter and typed FL scripting API.</summary>
public interface IPythonIdeExecution : IAsyncDisposable
{
    /// <summary>Runs trusted Python in FL's process and returns the framework's bounded result/output envelope.</summary>
    Task<JsonElement> ExecuteAsync(string code, int timeoutSeconds = 60, CancellationToken ct = default);
    /// <summary>Reads generated SDK metadata without starting Python or making native FL calls.</summary>
    Task<JsonElement> GetCatalogAsync(string? filter = null, CancellationToken ct = default);
    /// <summary>Requests cancellation and waits for Python and all active native work to drain.</summary>
    Task CancelAsync();
}
