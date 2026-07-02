using FruityLink.Core.Domain;
using FruityLink.Persistence;
using Shouldly;
using Xunit;

namespace FruityLink.Persistence.Tests;

public sealed class JsonVersionTreeTests : TempStorageFixture
{
    private readonly JsonVersionTree _tree;

    public JsonVersionTreeTests() => _tree = new JsonVersionTree(Paths);

    private static IReadOnlyList<ChatMessage> Messages(params string[] contents) =>
        contents.Select((c, i) => new ChatMessage($"m{i}", ChatRole.User, c, DateTimeOffset.UtcNow)).ToList();

    private static IReadOnlyList<FlOperation> Ops(params string[] descriptions) =>
        descriptions.Select(d => new FlOperation(
            FlOperationKind.WriteNotes, d, "pattern:1", UndoHint: null, DateTimeOffset.UtcNow)).ToList();

    [Fact]
    public async Task Create_root_then_child_links_correctly()
    {
        VersionNode root = await _tree.CreateCheckpointAsync(
            "s1", parentId: null, "root", Messages("hi"), Ops("created pattern"));

        VersionNode child = await _tree.CreateCheckpointAsync(
            "s1", parentId: root.Id, "after drums", Messages("hi", "add drums"), Ops("wrote 12 notes"));

        root.ParentId.ShouldBeNull();
        root.Id.ShouldNotBeNullOrEmpty();
        child.ParentId.ShouldBe(root.Id);
        child.Id.ShouldNotBe(root.Id);
        child.Label.ShouldBe("after drums");
        child.Messages.Count.ShouldBe(2);
        child.Operations[0].Description.ShouldBe("wrote 12 notes");
    }

    [Fact]
    public async Task GetTree_returns_all_nodes()
    {
        VersionNode root = await _tree.CreateCheckpointAsync("s2", null, "root", Messages("a"), Ops());
        VersionNode child1 = await _tree.CreateCheckpointAsync("s2", root.Id, "b", Messages("a", "b"), Ops());
        VersionNode child2 = await _tree.CreateCheckpointAsync("s2", root.Id, "c", Messages("a", "c"), Ops());

        IReadOnlyList<VersionNode> all = await _tree.GetTreeAsync("s2");

        all.Count.ShouldBe(3);
        all.Select(n => n.Id).ShouldBe([root.Id, child1.Id, child2.Id], ignoreOrder: true);
        all.Count(n => n.ParentId == root.Id).ShouldBe(2);
        all.Count(n => n.ParentId is null).ShouldBe(1);
    }

    [Fact]
    public async Task GetTree_for_unknown_session_is_empty()
    {
        (await _tree.GetTreeAsync("never")).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetNode_finds_one_and_returns_null_for_missing()
    {
        VersionNode root = await _tree.CreateCheckpointAsync("s3", null, "root", Messages("x"), Ops());

        VersionNode? found = await _tree.GetNodeAsync("s3", root.Id);
        VersionNode? missing = await _tree.GetNodeAsync("s3", "no-such-node");

        found.ShouldNotBeNull();
        found.Label.ShouldBe("root");
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task Create_with_unknown_parent_throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _tree.CreateCheckpointAsync("s4", parentId: "ghost", "x", Messages("x"), Ops()));
    }

    [Fact]
    public async Task Sessions_are_isolated()
    {
        await _tree.CreateCheckpointAsync("sa", null, "root-a", Messages("a"), Ops());
        await _tree.CreateCheckpointAsync("sb", null, "root-b", Messages("b"), Ops());

        (await _tree.GetTreeAsync("sa")).Count.ShouldBe(1);
        (await _tree.GetTreeAsync("sb")).Single().Label.ShouldBe("root-b");
    }
}
