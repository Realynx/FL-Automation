using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class PluginStateEvidenceTests
{
    [Fact]
    public void MissingSnapshotIsReportedAsNotComparedNeverAsUnchanged()
    {
        string line = FlInjectBridge.DescribeStateEvidence(null, new byte[] { 1, 2, 3 });
        Assert.StartsWith("state record unavailable", line);
        Assert.Contains("nothing was compared", line);
        Assert.DoesNotContain("unchanged", line);
        Assert.Equal(line, FlInjectBridge.DescribeStateEvidence(new byte[] { 1 }, null));
    }

    [Fact]
    public void IdenticalRecordsReportSizeAndHash()
    {
        byte[] state = "XferJson{...}"u8.ToArray();
        string line = FlInjectBridge.DescribeStateEvidence(state, state.ToArray());
        Assert.Equal($"state record unchanged ({state.Length} bytes, sha256 {FlInjectBridge.ShortHash(state)}; a wrong-format or already-active file leaves the state untouched)", line);
        Assert.Matches("^[0-9a-f]{8}$", FlInjectBridge.ShortHash(state));
    }

    [Fact]
    public void ChangedRecordsCountDifferingBytesAndSizeDelta()
    {
        byte[] before = new byte[100];
        byte[] after = new byte[110];
        after[10] = 1; after[11] = 2; after[50] = 3;              // 3 differing bytes in the common prefix
        string line = FlInjectBridge.DescribeStateEvidence(before, after);
        Assert.StartsWith("state record changed (100 -> 110 bytes, sha256 ", line);
        Assert.Contains("13 differing bytes = 11.8%, first at byte 10)", line);   // 3 + 10 extra bytes over max(100, 110)
        Assert.Contains($"{FlInjectBridge.ShortHash(before)} -> {FlInjectBridge.ShortHash(after)}", line);
    }

    [Fact]
    public void ShrunkRecordCountsTheRemovedTail()
    {
        byte[] before = new byte[8];
        byte[] after = new byte[6];
        string line = FlInjectBridge.DescribeStateEvidence(before, after);
        Assert.Contains("(8 -> 6 bytes", line);
        Assert.Contains("2 differing bytes = 25%, first at byte 6)", line);
    }
}
