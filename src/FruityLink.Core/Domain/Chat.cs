namespace FruityLink.Core.Domain;

/// <summary>Role of a chat message.</summary>
public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>A single chat message.</summary>
/// <param name="Id">Stable message id.</param>
/// <param name="Role">Who authored it.</param>
/// <param name="Content">Message text.</param>
/// <param name="Timestamp">When it was created.</param>
/// <param name="ToolName">For tool messages, the invoked tool's name.</param>
public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    string Content,
    DateTimeOffset Timestamp,
    string? ToolName = null);

/// <summary>A saved conversation with its message list and metadata.</summary>
/// <param name="Id">Stable session id.</param>
/// <param name="Title">Display title.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last modification time.</param>
/// <param name="Messages">Ordered messages.</param>
/// <param name="ActiveNodeId">Current node in the version tree, if branching is in use.</param>
public sealed record ChatSession(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ChatMessage> Messages,
    string? ActiveNodeId = null);
