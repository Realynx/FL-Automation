using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Persistence;

/// <summary>
/// File-backed <see cref="IChatStore"/> storing one <c>chats/{id}.json</c> per session.
/// Writes are atomic; <see cref="ExportAsync"/> renders Markdown transcripts or indented JSON.
/// </summary>
public sealed class JsonChatStore : IChatStore
{
    private readonly StoragePaths _paths;

    /// <summary>Creates a store rooted at the supplied <paramref name="paths"/>.</summary>
    /// <param name="paths">Shared storage layout (DI: register one instance for all stores).</param>
    public JsonChatStore(StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatSession>> ListAsync(CancellationToken ct = default)
    {
        List<ChatSession> sessions = [];
        foreach (string file in Directory.EnumerateFiles(_paths.ChatsDirectory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            ChatSession? session = await ReadFileAsync(file, ct).ConfigureAwait(false);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ChatSession?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string path = _paths.ChatFile(id);
        return File.Exists(path) ? await ReadFileAsync(path, ct).ConfigureAwait(false) : null;
    }

    /// <inheritdoc />
    public async Task SaveAsync(ChatSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(session.Id);
        string json = JsonSerializer.Serialize(session, JsonDefaults.Options);
        await AtomicFile.WriteAllTextAsync(_paths.ChatFile(session.Id), json, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        string path = _paths.ChatFile(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(string id, ChatExportFormat format, CancellationToken ct = default)
    {
        ChatSession session = await GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Chat session '{id}' was not found.");

        return format switch
        {
            ChatExportFormat.Json => JsonSerializer.Serialize(session, JsonDefaults.Options),
            ChatExportFormat.Markdown => RenderMarkdown(session),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format."),
        };
    }

    private static async Task<ChatSession?> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ChatSession>(stream, JsonDefaults.Options, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Skip corrupt/unparseable files rather than failing the whole listing.
            return null;
        }
    }

    private static string RenderMarkdown(ChatSession session)
    {
        StringBuilder sb = new();
        sb.Append("# ").AppendLine(session.Title);
        sb.AppendLine();
        sb.Append("- Session: `").Append(session.Id).AppendLine("`");
        sb.Append("- Created: ").AppendLine(session.CreatedAt.ToString("u"));
        sb.Append("- Updated: ").AppendLine(session.UpdatedAt.ToString("u"));
        sb.AppendLine();

        foreach (ChatMessage message in session.Messages)
        {
            string heading = message.Role switch
            {
                ChatRole.System => "System",
                ChatRole.User => "User",
                ChatRole.Assistant => "Assistant",
                ChatRole.Tool => message.ToolName is { Length: > 0 } tool
                    ? $"Tool ({tool})"
                    : "Tool",
                _ => message.Role.ToString(),
            };

            sb.Append("## ").Append(heading)
              .Append(" — ").AppendLine(message.Timestamp.ToString("u"));
            sb.AppendLine();
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
