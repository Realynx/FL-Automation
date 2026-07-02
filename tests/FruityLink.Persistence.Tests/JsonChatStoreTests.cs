using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using FruityLink.Persistence;
using Shouldly;
using Xunit;

namespace FruityLink.Persistence.Tests;

public sealed class JsonChatStoreTests : TempStorageFixture
{
    private readonly JsonChatStore _store;

    public JsonChatStoreTests() => _store = new JsonChatStore(Paths);

    private static ChatSession SampleSession(string id, DateTimeOffset updatedAt) => new(
        Id: id,
        Title: "My First Beat",
        CreatedAt: new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
        UpdatedAt: updatedAt,
        Messages:
        [
            new ChatMessage("m1", ChatRole.User, "Make a house groove", new DateTimeOffset(2026, 6, 1, 10, 0, 1, TimeSpan.Zero)),
            new ChatMessage("m2", ChatRole.Assistant, "Sure, adding a four-on-the-floor kick.", new DateTimeOffset(2026, 6, 1, 10, 0, 2, TimeSpan.Zero)),
            new ChatMessage("m3", ChatRole.Tool, "{ \"pattern\": 3 }", new DateTimeOffset(2026, 6, 1, 10, 0, 3, TimeSpan.Zero), ToolName: "CreatePattern"),
        ]);

    [Fact]
    public async Task Save_Get_round_trips_a_session()
    {
        ChatSession session = SampleSession("chat-1", DateTimeOffset.UtcNow);

        await _store.SaveAsync(session);
        ChatSession? loaded = await _store.GetAsync("chat-1");

        loaded.ShouldNotBeNull();
        loaded.Id.ShouldBe(session.Id);
        loaded.Title.ShouldBe(session.Title);
        loaded.Messages.Count.ShouldBe(3);
        loaded.Messages[2].ToolName.ShouldBe("CreatePattern");
        loaded.Messages[2].Role.ShouldBe(ChatRole.Tool);
    }

    [Fact]
    public async Task Get_missing_returns_null()
    {
        (await _store.GetAsync("does-not-exist")).ShouldBeNull();
    }

    [Fact]
    public async Task List_orders_by_updated_descending()
    {
        await _store.SaveAsync(SampleSession("old", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        await _store.SaveAsync(SampleSession("new", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));
        await _store.SaveAsync(SampleSession("mid", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));

        IReadOnlyList<ChatSession> all = await _store.ListAsync();

        all.Select(s => s.Id).ShouldBe(["new", "mid", "old"]);
    }

    [Fact]
    public async Task Delete_removes_the_session()
    {
        await _store.SaveAsync(SampleSession("chat-del", DateTimeOffset.UtcNow));
        (await _store.GetAsync("chat-del")).ShouldNotBeNull();

        await _store.DeleteAsync("chat-del");

        (await _store.GetAsync("chat-del")).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_missing_does_not_throw()
    {
        await Should.NotThrowAsync(() => _store.DeleteAsync("never-existed"));
    }

    [Fact]
    public async Task Export_markdown_contains_title_and_message_text()
    {
        ChatSession session = SampleSession("chat-md", DateTimeOffset.UtcNow);
        await _store.SaveAsync(session);

        string markdown = await _store.ExportAsync("chat-md", ChatExportFormat.Markdown);

        markdown.ShouldContain("# My First Beat");
        markdown.ShouldContain("Make a house groove");
        markdown.ShouldContain("Sure, adding a four-on-the-floor kick.");
        markdown.ShouldContain("Tool (CreatePattern)");
    }

    [Fact]
    public async Task Export_json_deserializes_back_to_session()
    {
        ChatSession session = SampleSession("chat-json", DateTimeOffset.UtcNow);
        await _store.SaveAsync(session);

        string json = await _store.ExportAsync("chat-json", ChatExportFormat.Json);
        ChatSession? roundTripped = JsonSerializer.Deserialize<ChatSession>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });

        roundTripped.ShouldNotBeNull();
        roundTripped.Id.ShouldBe("chat-json");
        roundTripped.Messages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Export_missing_throws()
    {
        await Should.ThrowAsync<FileNotFoundException>(
            () => _store.ExportAsync("nope", ChatExportFormat.Markdown));
    }
}
