using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// The model-facing read-only view over the agent's own project version history. These tests pin
/// the envelope (OK/ERR), the id-resolution rules (head / prefix / ambiguity / too-short), the
/// ancestor-range walk (oldest first; other-branch ids rejected, not diffed), and the output caps —
/// the contract weak backends parse, not exact formatting.
/// </summary>
public sealed class VersioningPluginTests
{
    [Fact]
    public void Tools_ReturnErr_BeforeAStoreIsAttached()
    {
        var plugin = new VersioningPlugin();

        plugin.ListVersions().ShouldStartWith("ERR:");
        plugin.GetVersionChanges("head").ShouldStartWith("ERR:");
    }

    [Fact]
    public void ListVersions_OnEmptyHistory_IsOkAndExplainsWhenHistoryStarts()
    {
        VersioningPlugin plugin = Attached(new FakeVersionControl());

        string result = plugin.ListVersions();

        result.ShouldStartWith("OK:");
        result.ShouldContain("no versions yet");
    }

    [Fact]
    public void ListVersions_ShowsNewestFirst_WithHeadMarkerAndOpCounts()
    {
        var vc = LinearHistory("aaaa1111", "bbbb2222", "cccc3333");
        VersioningPlugin plugin = Attached(vc);

        string result = plugin.ListVersions();

        result.ShouldStartWith("OK: 3 version(s)");
        int newest = result.IndexOf("cccc3333", StringComparison.Ordinal);
        int oldest = result.IndexOf("aaaa1111", StringComparison.Ordinal);
        newest.ShouldBeGreaterThan(0);
        newest.ShouldBeLessThan(oldest);
        result.ShouldContain("cccc3333 [HEAD]");
        result.ShouldContain("(2 ops)");   // every fake commit carries 2 ops
    }

    [Fact]
    public void ListVersions_ClampsCount_AndReportsHowManyAreShown()
    {
        VersioningPlugin plugin = Attached(LinearHistory("aaaa1111", "bbbb2222", "cccc3333"));

        string result = plugin.ListVersions(count: 2);

        result.ShouldContain("showing newest 2");
        result.ShouldContain("cccc3333");
        result.ShouldContain("bbbb2222");
        result.ShouldNotContain("aaaa1111");
    }

    [Fact]
    public void GetVersionChanges_SingleVersion_ListsItsOps()
    {
        VersioningPlugin plugin = Attached(LinearHistory("aaaa1111", "bbbb2222"));

        string result = plugin.GetVersionChanges("bbbb2222");

        result.ShouldStartWith("OK:");
        result.ShouldContain("- op1 of bbbb2222");
        result.ShouldContain("- op2 of bbbb2222");
        result.ShouldNotContain("aaaa1111");
    }

    [Fact]
    public void GetVersionChanges_HeadKeywordAndPrefix_ResolveTheSameCommit()
    {
        VersioningPlugin plugin = Attached(LinearHistory("aaaa1111", "bbbb2222"));

        plugin.GetVersionChanges("head").ShouldContain("op1 of bbbb2222");
        plugin.GetVersionChanges("bbbb").ShouldContain("op1 of bbbb2222");
    }

    [Fact]
    public void GetVersionChanges_Range_WalksAncestorsOldestFirst_ExcludingSince()
    {
        VersioningPlugin plugin = Attached(LinearHistory("aaaa1111", "bbbb2222", "cccc3333"));

        string result = plugin.GetVersionChanges("head", since: "aaaa1111");

        result.ShouldStartWith("OK:");
        result.ShouldContain("2 version(s), 4 ops");
        result.ShouldNotContain("op1 of aaaa1111");   // 'since' itself is excluded
        int b = result.IndexOf("op1 of bbbb2222", StringComparison.Ordinal);
        int c = result.IndexOf("op1 of cccc3333", StringComparison.Ordinal);
        b.ShouldBeGreaterThan(0);
        b.ShouldBeLessThan(c);   // oldest first — the order the work was performed
    }

    [Fact]
    public void GetVersionChanges_SinceOnAnotherBranch_FailsWithACorrectiveHint()
    {
        // aaaa1111 → bbbb2222 and aaaa1111 → dddd4444 (branch); head = dddd4444.
        var commits = new List<ProjectCommit>
        {
            Commit("aaaa1111", parent: null),
            Commit("bbbb2222", parent: "aaaa1111"),
            Commit("dddd4444", parent: "aaaa1111"),
        };
        VersioningPlugin plugin = Attached(new FakeVersionControl(commits, headId: "dddd4444"));

        string result = plugin.GetVersionChanges("dddd4444", since: "bbbb2222");

        result.ShouldStartWith("ERR:");
        result.ShouldContain("not an ancestor");
    }

    [Fact]
    public void GetVersionChanges_BadIds_FailWithSelfCorrectingErrors()
    {
        VersioningPlugin plugin = Attached(LinearHistory("aaaa1111", "aaab2222"));

        plugin.GetVersionChanges("ffff9999").ShouldContain("no version matches");
        plugin.GetVersionChanges("aaa").ShouldContain("too short");
        plugin.GetVersionChanges("aaa" + "a").ShouldNotContain("ambiguous");   // 'aaaa' → unique
        plugin.GetVersionChanges("aaab2222", since: "aaab2222").ShouldContain("same version");
    }

    [Fact]
    public void GetVersionChanges_CapsOpLines_AndReportsTheRemainder()
    {
        var ops = Enumerable.Range(1, 40).Select(i => $"bulk op {i}").ToArray();
        var commits = new List<ProjectCommit> { Commit("aaaa1111", parent: null, ops) };
        VersioningPlugin plugin = Attached(new FakeVersionControl(commits, headId: "aaaa1111"));

        string result = plugin.GetVersionChanges("head");

        result.ShouldContain("bulk op 25");
        result.ShouldNotContain("bulk op 26");
        result.ShouldContain("+15 more");
    }

    // ---------------- helpers ----------------

    private static VersioningPlugin Attached(IProjectVersionControl vc)
    {
        var plugin = new VersioningPlugin();
        plugin.Attach(vc);
        return plugin;
    }

    /// <summary>ids become a parent chain in the given (oldest → newest) order; last id is HEAD.</summary>
    private static FakeVersionControl LinearHistory(params string[] ids)
    {
        var commits = new List<ProjectCommit>();
        for (int i = 0; i < ids.Length; i++)
            commits.Add(Commit(ids[i], parent: i == 0 ? null : ids[i - 1]));
        return new FakeVersionControl(commits, headId: ids[^1]);
    }

    private static ProjectCommit Commit(string id, string? parent, IReadOnlyList<string>? ops = null) =>
        new(
            Id: id,
            ParentId: parent,
            Label: $"label {id}",
            CreatedAt: DateTimeOffset.UtcNow,
            SessionId: "default",
            ChatNodeId: null,
            FlpBackupPath: $"{id}.flp",
            StateJsonPath: null,
            OriginalProjectPath: null,
            Operations: ops ?? new[] { $"op1 of {id}", $"op2 of {id}" },
            Trigger: CommitTrigger.Auto);

    private sealed class FakeVersionControl(
        IReadOnlyList<ProjectCommit>? history = null, string? headId = null) : IProjectVersionControl
    {
        public string SessionId => "default";
        public IReadOnlyList<ProjectCommit> History { get; } = history ?? Array.Empty<ProjectCommit>();
        public ProjectCommit? Head => History.FirstOrDefault(c => c.Id == headId);
        public bool CanUndo => false;
        public bool CanRedo => false;
        public ProjectCommit? RecoveryCandidate => null;
        public event EventHandler<ProjectVersionChanged>? Changed { add { } remove { } }

        public Task OpenSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ProjectCommit?> CommitAsync(string? label = null, string? chatNodeId = null,
            IReadOnlyList<string>? operations = null, CommitTrigger trigger = CommitTrigger.Manual,
            CancellationToken ct = default) => Task.FromResult<ProjectCommit?>(null);
        public Task<ProjectCommit?> UndoAsync(CancellationToken ct = default) => Task.FromResult<ProjectCommit?>(null);
        public Task<ProjectCommit?> RedoAsync(CancellationToken ct = default) => Task.FromResult<ProjectCommit?>(null);
        public Task<ProjectCommit?> RestoreAsync(string commitId, CancellationToken ct = default) => Task.FromResult<ProjectCommit?>(null);
        public Task<FlProjectState?> GetStateAsync(string commitId, CancellationToken ct = default) => Task.FromResult<FlProjectState?>(null);
        public void DismissRecovery() { }
    }
}
