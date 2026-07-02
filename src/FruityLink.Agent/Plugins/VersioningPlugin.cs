using System.ComponentModel;
using System.Text;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using Microsoft.SemanticKernel;
using static FruityLink.Agent.Plugins.PluginSupport;

namespace FruityLink.Agent.Plugins;

/// <summary>
/// Read-only window onto the agent's own project version history (the auto-snapshot
/// <c>ProjectVersionCoordinator</c> commits after each mutating turn), so the model can recall and
/// compare what IT already changed without re-reading live project state. Deliberately TWO tools —
/// list + inspect/range — because every extra tool schema is a per-turn token cost: one lister and
/// one "what changed" tool cover recall, inspection AND comparison (a range over the commit DAG).
/// Read-only on purpose: restore/undo is the user's History panel, not the model's.
/// The store is attached late via <see cref="Attach"/> (it is composed after the plugin set because
/// it needs the live bridge); until then — and in hosts without version control — tools answer with
/// a self-explanatory ERR rather than the advertised surface changing shape.
/// </summary>
public sealed class VersioningPlugin
{
    /// <summary>Chars of a commit GUID shown to the model: unique among a session's handful of
    /// commits, short enough not to waste result tokens.</summary>
    private const int ShortIdLength = 8;

    /// <summary>Shortest accepted id prefix, so a stray 1-2 char argument can't "resolve".</summary>
    private const int MinPrefixLength = 4;

    /// <summary>Per-version cap on op lines in get_version_changes — keeps a fat turn's history
    /// scannable and comfortably under ToolCallFilter's 6KB result truncation.</summary>
    private const int MaxOpsShown = 25;

    private volatile IProjectVersionControl? _vc;

    /// <summary>Late-binds the store (see <c>FlAgent.AttachVersionControl</c>).</summary>
    public void Attach(IProjectVersionControl versionControl) => _vc = versionControl;

    [KernelFunction("list_versions")]
    [Description("List saved project versions — one auto-snapshot per mutating turn of yours, newest first: id, age, label, op count; HEAD = the live state. Inspect/compare with get_version_changes.")]
    public string ListVersions(
        [Description("Max versions to show, newest first")] int count = 10)
    {
        if (_vc is not { } vc) return Err("version history unavailable in this host");
        IReadOnlyList<ProjectCommit> history = vc.History;
        if (history.Count == 0)
            return Ok("no versions yet — the first snapshot commits after your first mutating turn");

        count = Math.Clamp(count, 1, 100);
        int shown = Math.Min(count, history.Count);
        string? headId = vc.Head?.Id;

        var sb = new StringBuilder();
        sb.Append(history.Count).Append(" version(s)");
        if (shown < history.Count) sb.Append(", showing newest ").Append(shown);
        sb.Append(':');
        for (int i = history.Count - 1; i >= history.Count - shown; i--)
        {
            ProjectCommit c = history[i];
            sb.Append('\n').Append(ShortId(c.Id));
            if (string.Equals(c.Id, headId, StringComparison.OrdinalIgnoreCase)) sb.Append(" [HEAD]");
            sb.Append(' ').Append(Age(c.CreatedAt));
            if (TriggerNote(c.Trigger) is { } note) sb.Append(' ').Append(note);
            sb.Append(" \"").Append(c.Label).Append("\" (").Append(c.Operations.Count).Append(" ops)");
        }
        return Ok(sb.ToString());
    }

    [KernelFunction("get_version_changes")]
    [Description("Show your recorded operations in one version, or every operation between two ('since' exclusive, oldest first). Check before re-editing earlier work instead of re-reading project state. Ids from list_versions; 'head' works.")]
    public string GetVersionChanges(
        [Description("Version id (prefix ok) or 'head'")] string id,
        [Description("Older ancestor version id; empty = this version only")] string since = "")
    {
        if (_vc is not { } vc) return Err("version history unavailable in this host");
        IReadOnlyList<ProjectCommit> history = vc.History;
        if (history.Count == 0)
            return Ok("no versions yet — the first snapshot commits after your first mutating turn");

        if (Resolve(vc, history, id, out ProjectCommit? target) is { } idError) return idError;
        if (string.IsNullOrWhiteSpace(since))
            return Ok(Describe(target!, new StringBuilder()).ToString());

        if (Resolve(vc, history, since, out ProjectCommit? from) is { } sinceError) return sinceError;
        if (string.Equals(from!.Id, target!.Id, StringComparison.OrdinalIgnoreCase))
            return Ok($"{ShortId(target.Id)}: 'since' is the same version — no changes between them");

        // Walk target → parents, collecting until `since` (exclusive). History is a DAG mirroring
        // the chat tree, so a version on another branch is NOT an ancestor — fail loudly with a
        // corrective hint rather than fabricate a diff from unrelated commits.
        var byId = new Dictionary<string, ProjectCommit>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectCommit c in history) byId[c.Id] = c;

        var chain = new List<ProjectCommit>();
        bool found = false;
        for (ProjectCommit? cur = target; cur is not null;)
        {
            if (string.Equals(cur.Id, from.Id, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
            chain.Add(cur);
            cur = cur.ParentId is { } p && byId.TryGetValue(p, out ProjectCommit? parent) ? parent : null;
        }
        if (!found)
            return Err($"{ShortId(from.Id)} is not an ancestor of {ShortId(target.Id)} — it is newer or on another branch; pick an older id from list_versions");

        chain.Reverse();   // oldest first, so ops read in the order they were performed
        var sb = new StringBuilder();
        sb.Append(ShortId(from.Id)).Append(" → ").Append(ShortId(target.Id))
          .Append(": ").Append(chain.Count).Append(" version(s), ")
          .Append(chain.Sum(c => c.Operations.Count)).Append(" ops, oldest first");
        foreach (ProjectCommit c in chain)
        {
            sb.Append('\n');
            Describe(c, sb);
        }
        return Ok(sb.ToString());
    }

    /// <summary>One version's header + op lines (capped at <see cref="MaxOpsShown"/>).</summary>
    private static StringBuilder Describe(ProjectCommit commit, StringBuilder sb)
    {
        sb.Append('[').Append(ShortId(commit.Id)).Append(" \"").Append(commit.Label)
          .Append("\" ").Append(Age(commit.CreatedAt)).Append(']');
        if (commit.Operations.Count == 0)
            return sb.Append("\n(no recorded ops)");

        int show = Math.Min(commit.Operations.Count, MaxOpsShown);
        for (int i = 0; i < show; i++)
            sb.Append("\n- ").Append(commit.Operations[i]);
        if (show < commit.Operations.Count)
            sb.Append("\n… +").Append(commit.Operations.Count - show).Append(" more");
        return sb;
    }

    /// <summary>Resolves 'head' / an id prefix to a commit. Returns an ERR string (with the fix
    /// spelled out) or null on success — the Guard convention, shaped for two out-args.</summary>
    private static string? Resolve(
        IProjectVersionControl vc, IReadOnlyList<ProjectCommit> history, string idOrHead, out ProjectCommit? commit)
    {
        commit = null;
        string key = (idOrHead ?? string.Empty).Trim();
        if (key.Length == 0 || key.Equals("head", StringComparison.OrdinalIgnoreCase))
        {
            commit = vc.Head;
            return commit is null ? Err("no HEAD — no versions committed yet") : null;
        }
        if (key.Length < MinPrefixLength)
            return Err($"version id '{key}' too short — use at least {MinPrefixLength} chars from list_versions");

        var matches = history.Where(c => c.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) { commit = matches[0]; return null; }
        return matches.Count == 0
            ? Err($"no version matches '{key}' — call list_versions for valid ids")
            : Err($"'{key}' is ambiguous ({matches.Count} matches) — use a longer prefix");
    }

    private static string ShortId(string id) =>
        id.Length <= ShortIdLength ? id : id[..ShortIdLength];

    /// <summary>Non-Auto commits get a one-word origin tag so the model doesn't misread a manual
    /// save or pre-restore safety copy as its own turn's work.</summary>
    private static string? TriggerNote(CommitTrigger trigger) => trigger switch
    {
        CommitTrigger.Initial => "(baseline)",
        CommitTrigger.Manual => "(manual)",
        CommitTrigger.PreRestoreSafety => "(pre-restore)",
        _ => null,
    };

    private static string Age(DateTimeOffset createdAt)
    {
        TimeSpan age = DateTimeOffset.UtcNow - createdAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return age.TotalSeconds < 90 ? "just now"
            : age.TotalMinutes < 90 ? $"{Math.Round(age.TotalMinutes)}m ago"
            : age.TotalHours < 36 ? $"{Math.Round(age.TotalHours)}h ago"
            : $"{Math.Round(age.TotalDays)}d ago";
    }
}
