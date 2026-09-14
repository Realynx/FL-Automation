using System.Diagnostics;
using Xunit;

namespace FruityLink.Plugins.Host.Tests;

public sealed class ShadowCopyStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fl-shadow-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pluginsDir;
    private readonly string _shadowRoot;
    private readonly List<string> _log = [];

    public ShadowCopyStoreTests()
    {
        _pluginsDir = Path.Combine(_root, "plugins");
        _shadowRoot = Path.Combine(_root, "plugin-shadow");
        Directory.CreateDirectory(Path.Combine(_pluginsDir, "fixture"));
        File.WriteAllBytes(Path.Combine(_pluginsDir, "fixture", "Fixture.dll"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(_pluginsDir, "fixture", "Dep.dll"), new byte[] { 4, 5 });
        Directory.CreateDirectory(_shadowRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeCopy(string name, int? ownerPid, long ownerStart = 0)
    {
        string dir = Path.Combine(_shadowRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Fixture.dll"), new byte[] { 1, 2, 3 });
        if (ownerPid is int pid) ShadowCopyStore.WriteOwnerMarker(dir, pid, ownerStart);
        return dir;
    }

    private static (int pid, long startTicks) CurrentOwner()
    {
        using Process me = Process.GetCurrentProcess();
        return (me.Id, me.StartTime.ToUniversalTime().Ticks);
    }

    [Fact]
    public void ConstructionPrunesCopiesWhoseOwnerProcessIsGone()
    {
        string dead = MakeCopy("dead", ownerPid: int.MaxValue - 1);
        (int pid, long start) = CurrentOwner();
        string live = MakeCopy("live", pid, start);
        string reusedPid = MakeCopy("reused-pid", pid, start + TimeSpan.TicksPerHour);

        _ = new ShadowCopyStore(_shadowRoot, _pluginsDir, _log.Add);

        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(live));
        Assert.False(Directory.Exists(reusedPid));
        Assert.Contains(_log, line => line.Contains("pruned 2 stale plugin copies", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyCopyWithoutMarkerIsKeptWhileLockedAndPrunedAfterwards()
    {
        string legacy = MakeCopy("legacy", ownerPid: null);
        string free = MakeCopy("legacy-free", ownerPid: null);

        var store = new ShadowCopyStore(_shadowRoot, _pluginsDir, _log.Add, pruneStale: false);
        using (new FileStream(Path.Combine(legacy, "Fixture.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, store.PruneStale());
            Assert.True(Directory.Exists(legacy));
            Assert.False(Directory.Exists(free));
        }

        Assert.Equal(1, store.PruneStale());
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void ShadowCopyWritesAnOwnerMarkerForThisProcess()
    {
        var store = new ShadowCopyStore(_shadowRoot, _pluginsDir, _log.Add);
        (string dir, string dll) = store.ShadowCopy(Path.Combine(_pluginsDir, "fixture", "Fixture.dll"));

        Assert.True(File.Exists(dll));
        Assert.True(File.Exists(Path.Combine(dir, "Dep.dll")));
        string[] marker = File.ReadAllLines(Path.Combine(dir, ShadowCopyStore.OwnerMarkerFileName));
        Assert.Equal(Environment.ProcessId, int.Parse(marker[0]));
        Assert.False(ShadowCopyStore.IsStale(dir));

        // A second host starting now must not touch a live copy.
        _ = new ShadowCopyStore(_shadowRoot, _pluginsDir, _log.Add);
        Assert.True(File.Exists(dll));
    }
}
