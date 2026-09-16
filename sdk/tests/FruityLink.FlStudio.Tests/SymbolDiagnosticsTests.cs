using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class SymbolDiagnosticsTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;

    public void Dispose() => FlInjectBridge.Transport = original;

    [Fact]
    public async Task CompletedUnsupportedScanIsAuthoritativeAndCachedWhenEverySymbolFailed()
    {
        var names = Enumerable.Range(0, 96).Select(index => $"MissingSymbol{index}").ToArray();
        var response = JsonSerializer.Serialize(new
        {
            ver = 0, fileVersion = "27.0.0.1", scanner = "unsupported", supported = false,
            complete = true, ok = 0, fail = names.Length, mixerTrackStride = (int?)null,
            unresolved = names.Select(name => new { name, why = "UnsupportedVersion" })
        });
        var calls = 0;
        FlInjectBridge.Transport = (_, _, _) => { calls++; return Task.FromResult(response); };

        var status = await new FlInjectBridge().GetSymbolStatusAsync();
        Assert.NotNull(status);
        Assert.True(status.Complete);
        Assert.False(status.Supported);
        Assert.Equal("27.0.0.1", status.FileVersion);
        Assert.Equal("unsupported", status.Scanner);
        Assert.Equal(0, status.Resolved);
        Assert.Equal(96, status.Failed);
        Assert.Equal(96, status.Unresolved.Count);
        Assert.Null(status.MixerTrackStride);
        Assert.Null(status.MixerLayout);
        Assert.All(names, name => Assert.Contains(name, status.Unresolved));
        Assert.Same(status, await new FlInjectBridge().GetSymbolStatusAsync());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(96)]
    public async Task ExplicitPendingScanIsRetriedEvenIfSomeSymbolsHaveResolved(int resolved)
    {
        var calls = 0;
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult(++calls == 1
            ? JsonSerializer.Serialize(new { ver = 2, ok = resolved, fail = 0, complete = false, supported = true })
            : """{"ver":0,"ok":95,"fail":1,"complete":true,"supported":true,"scanner":"fl-2026","fileVersion":"26.1.3.5570","mixerTrackStride":5240}""");

        var bridge = new FlInjectBridge();
        Assert.Null(await bridge.GetSymbolStatusAsync());
        var status = await bridge.GetSymbolStatusAsync();
        Assert.NotNull(status);
        Assert.Equal("fl-2026", status.Scanner);
        Assert.Equal("26.1.3.5570", status.FileVersion);
        Assert.Equal(0, status.Version);
        Assert.Equal(0x1478, status.MixerTrackStride);
        Assert.Same(status, await bridge.GetSymbolStatusAsync());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CompleteSupportedScanWithNoMatchesIsNotTreatedAsPending()
    {
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult(
            """{"ver":0,"ok":0,"fail":1,"complete":true,"supported":true,"unresolved":[{"name":"FL_DispatchCommand","why":"NotFound"}]}""");

        var status = await new FlInjectBridge().GetSymbolStatusAsync();
        Assert.NotNull(status);
        Assert.True(status.Complete);
        Assert.True(status.Supported);
        Assert.Contains("FL_DispatchCommand", status.Unresolved);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"ok\":96,\"complete\":\"true\"}")]
    [InlineData("{\"ok\":96,\"supported\":1}")]
    [InlineData("{\"ok\":96,\"fileVersion\":2601}")]
    [InlineData("{\"ok\":96,\"scanner\":{}}")]
    [InlineData("{\"ok\":96,\"mixerTrackStride\":\"5240\"}")]
    [InlineData("{\"ok\":96,\"mixerTrackStride\":-1}")]
    [InlineData("{\"ok\":96,\"mixerTrackStride\":0}")]
    [InlineData("{\"ok\":96,\"mixerTrackStride\":2147483648}")]
    public async Task MalformedDiagnosticsAreNotCached(string malformed)
    {
        var calls = 0;
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult(++calls == 1 ? malformed :
            """{"ver":1,"ok":96,"fail":0,"complete":true,"supported":true}""");

        var bridge = new FlInjectBridge();
        Assert.Null(await bridge.GetSymbolStatusAsync());
        Assert.True((await bridge.GetSymbolStatusAsync())!.Complete);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 0x1474)]
    [InlineData(2, 0x1478)]
    [InlineData(3, null)]
    public void MissingStrideUsesOnlyKnownLegacyExactVersion(int version, int? expected)
    {
        var status = FlInjectBridge.ParseSyms(JsonSerializer.Serialize(new { ver = version, ok = 1 }));
        Assert.NotNull(status);
        Assert.Equal(expected, status.MixerTrackStride);
        Assert.Null(status.Complete);
        Assert.Null(status.Supported);
        Assert.Null(status.Scanner);
        Assert.Null(status.FileVersion);
        Assert.Null(status.MixerLayout);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ExplicitNullStrideDoesNotInheritLegacyLayout(int version)
    {
        var status = FlInjectBridge.ParseSyms(JsonSerializer.Serialize(new { ver = version, ok = 1, mixerTrackStride = (int?)null }));
        Assert.NotNull(status);
        Assert.Null(status.MixerTrackStride);
    }

    [Fact]
    public void ExplicitProfileStrideOverridesLegacyExactVersion()
    {
        var status = FlInjectBridge.ParseSyms("""{"ver":1,"ok":1,"mixerTrackStride":5240}""");
        Assert.Equal(0x1478, status!.MixerTrackStride);
    }

    [Fact]
    public void RecordKeepsFourArgumentConstructionAndDeconstruction()
    {
        var expectedNames = new HashSet<string>(StringComparer.Ordinal) { "Missing" };
        var status = new FlSymbolStatus(2, 95, 1, expectedNames)
        {
            Complete = true, Supported = true, FileVersion = "26.1.0.5530", Scanner = "fl-2026", MixerTrackStride = 0x1478
        };
        var (version, resolved, failed, names) = status;
        Assert.Equal(2, version);
        Assert.Equal(95, resolved);
        Assert.Equal(1, failed);
        Assert.Same(expectedNames, names);
    }

    [Fact]
    public async Task ReplacingTransportInvalidatesCompletedAllFailureCache()
    {
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult(
            """{"ver":0,"ok":0,"fail":96,"complete":true,"supported":false}""");
        Assert.False((await new FlInjectBridge().GetSymbolStatusAsync())!.Supported);
        FlInjectBridge.Transport = (_, _, _) => Task.FromResult(
            """{"ver":1,"ok":96,"fail":0,"complete":true,"supported":true}""");
        Assert.True((await new FlInjectBridge().GetSymbolStatusAsync())!.Supported);
    }

    [Fact]
    public void ParsesCompleteMixerLayoutWithoutInferringItFromLegacyVersion()
    {
        var fields = MixerLayoutFields();
        var status = FlInjectBridge.ParseSyms(JsonSerializer.Serialize(new { ver = 0, ok = 95, complete = true, mixerLayout = fields }));
        Assert.NotNull(status);
        Assert.Equal(ExpectedMixerLayout, status.MixerLayout);
    }

    [Fact]
    public void PartialMixerLayoutIsMalformedForEveryMissingField()
    {
        foreach (var field in MixerLayoutFields().Keys)
        {
            var fields = MixerLayoutFields();
            fields.Remove(field);
            Assert.Null(FlInjectBridge.ParseSyms(JsonSerializer.Serialize(new { ver = 2, ok = 96, complete = true, mixerLayout = fields })));
        }
    }

    [Theory]
    [InlineData("trackStride", 0)]
    [InlineData("nameOffset", -1)]
    [InlineData("nameOffset", int.MaxValue)]
    [InlineData("typeOffset", 5238)]
    [InlineData("enabledOffset", 5240)]
    [InlineData("soloOffset", 5240)]
    [InlineData("sendTableOffset", 5239)]
    [InlineData("effectSlotsOffset", 5200)]
    [InlineData("sendStride", int.MaxValue)]
    [InlineData("sendLevelOffset", 5)]
    [InlineData("sendActiveOffset", 8)]
    [InlineData("effectSlotStride", 4)]
    [InlineData("effectSlotStride", int.MaxValue)]
    [InlineData("effectIndexOffset", -1)]
    [InlineData("effectNameOffset", -1)]
    [InlineData("effectLoadVtableOffset", -1)]
    public void RejectsMixerLayoutFieldsOutsideTheirStructure(string field, int value)
    {
        var fields = MixerLayoutFields();
        fields[field] = value;
        Assert.Null(FlInjectBridge.ParseSyms(JsonSerializer.Serialize(new { ok = 96, complete = true, mixerLayout = fields })));
    }

    [Fact]
    public void NullMixerLayoutDoesNotInheritLegacyStrideOrVersion()
    {
        var status = FlInjectBridge.ParseSyms("""{"ver":2,"ok":96,"mixerTrackStride":5240,"mixerLayout":null}""");
        Assert.NotNull(status);
        Assert.Equal(0x1478, status.MixerTrackStride);
        Assert.Null(status.MixerLayout);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("5240")]
    public void RejectsNonObjectMixerLayout(string value)
    {
        Assert.Null(FlInjectBridge.ParseSyms("{\"ok\":96,\"complete\":true,\"mixerLayout\":" + value + "}"));
    }

    private static FlMixerLayout ExpectedMixerLayout => new(
        TrackStride: 0x1478, NameOffset: 0x0c, TypeOffset: 0x08, EnabledOffset: 0x18, SoloOffset: 0x1a,
        SendTableOffset: 0x394, EffectSlotsOffset: 0x134c, SendStride: 8, SendLevelOffset: 0,
        SendActiveOffset: 4, EffectSlotStride: 8, EffectIndexOffset: 0x64, EffectNameOffset: 0x58,
        EffectLoadVtableOffset: 0xf0);

    private static Dictionary<string, int> MixerLayoutFields() =>
        JsonSerializer.SerializeToElement(ExpectedMixerLayout, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Deserialize<Dictionary<string, int>>()!;
}
