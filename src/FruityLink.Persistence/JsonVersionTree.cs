using System.Collections.Concurrent;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Persistence;

/// <summary>
/// File-backed <see cref="IVersionTree"/> storing a session's checkpoint DAG as a
/// <c>List&lt;VersionNode&gt;</c> in <c>versions/{sessionId}.json</c>. A per-session lock
/// serializes read-modify-write so concurrent checkpoint creation stays consistent.
/// </summary>
public sealed class JsonVersionTree : IVersionTree
{
    private readonly StoragePaths _paths;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Creates a version tree rooted at the supplied <paramref name="paths"/>.</summary>
    /// <param name="paths">Shared storage layout (DI: register one instance for all stores).</param>
    public JsonVersionTree(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<VersionNode> CreateCheckpointAsync(
        string sessionId,
        string? parentId,
        string label,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<FlOperation> operations,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(operations);

        VersionNode node = new(
            Id: Guid.NewGuid().ToString("N"),
            ParentId: parentId,
            Label: label,
            Messages: messages.ToList(),
            Operations: operations.ToList(),
            CreatedAt: DateTimeOffset.UtcNow);

        SemaphoreSlim gate = LockFor(sessionId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<VersionNode> nodes = await ReadNodesAsync(sessionId, ct).ConfigureAwait(false);

            if (parentId is not null && !nodes.Any(n => n.Id == parentId))
            {
                throw new InvalidOperationException(
                    $"Parent node '{parentId}' was not found in session '{sessionId}'.");
            }

            nodes.Add(node);
            await WriteNodesAsync(sessionId, nodes, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        return node;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VersionNode>> GetTreeAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        return await ReadNodesAsync(sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VersionNode?> GetNodeAsync(string sessionId, string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        List<VersionNode> nodes = await ReadNodesAsync(sessionId, ct).ConfigureAwait(false);
        return nodes.FirstOrDefault(n => n.Id == nodeId);
    }

    private SemaphoreSlim LockFor(string sessionId) =>
        _locks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));

    private async Task<List<VersionNode>> ReadNodesAsync(string sessionId, CancellationToken ct)
    {
        string path = _paths.VersionFile(sessionId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            List<VersionNode>? nodes = await JsonSerializer
                .DeserializeAsync<List<VersionNode>>(stream, JsonDefaults.Options, ct)
                .ConfigureAwait(false);
            return nodes ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task WriteNodesAsync(string sessionId, List<VersionNode> nodes, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(nodes, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(_paths.VersionFile(sessionId), json, ct).ConfigureAwait(false);
    }
}
