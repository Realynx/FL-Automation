using System.Text;
using System.Text.Json;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class StructuredQueryTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;
    private readonly Dictionary<string, string> replies = [];
    private readonly List<string> commands = [];

    public StructuredQueryTests() => FlInjectBridge.Transport = SendAsync;
    public void Dispose() => FlInjectBridge.Transport = original;

    [Fact]
    public async Task ProjectIdentityPreservesNewlinesAndUnicodeWithoutParsingDisplayText()
    {
        const string title = "Title: drums\nPath: a misleading line 🎹";
        const string path = "C:\\sessions\\東京\\drums.flp";
        replies["peek 1581298 8"] = Hex(0x30000UL);
        replies["peek 15812a0 8"] = Hex(0x40000UL);
        AddString(0x30000, path);
        AddString(0x40000, title);

        var result = await new FlInjectBridge().QueryProjectAsync();

        Assert.Equal(path, result.Path);
        Assert.Equal(title, result.Title);
        Assert.False(result.Untitled);
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(title, JsonDocument.Parse(json).RootElement.GetProperty("title").GetString());
        Assert.All(commands, command => Assert.StartsWith("peek", command));
    }

    [Fact]
    public async Task FilteredEmptyNotePageAdvancesRawOffsetAndPreservesNativeIdentity()
    {
        ConfigureNotes(3);
        replies["peekabs 30000 48"] = Convert.ToHexString(Note(2, 61, 0).Concat(Note(2, 64, 24)).ToArray());
        replies["peekabs 30030 24"] = Convert.ToHexString(Note(7, 67, 48, true));
        var bridge = new FlInjectBridge();

        var first = await bridge.QueryNotesAsync(3, channel: 7, limit: 2);
        var last = await bridge.QueryNotesAsync(3, channel: 7, offset: first.NextOffset!.Value, limit: 2);

        Assert.Empty(first.Items);
        Assert.Equal(2, first.NextOffset);
        Assert.Equal(3, first.Total);
        var note = Assert.Single(last.Items);
        Assert.Equal((2, 7, 67, 48, 96, 100, true),
            (note.Index, note.Channel, note.Key, note.StartTick, note.LengthTick, note.Velocity, note.Muted));
        Assert.Null(last.NextOffset);
    }

    [Fact]
    public async Task LongProjectPathIsNotReportedAsUntitled()
    {
        string path = "C:\\" + string.Concat(Enumerable.Repeat("a-directory\\", 40)) + "song.flp";
        replies["peek 1581298 8"] = Hex(0x30000UL);
        replies["peek 15812a0 8"] = Hex(0UL);
        AddString(0x30000, path);
        var project = await new FlInjectBridge().QueryProjectAsync();
        Assert.Equal(path, project.Path);
        Assert.False(project.Untitled);
        Assert.False(await new FlInjectBridge().IsUntitledProjectAsync());
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 513)]
    public async Task InvalidPaginationFailsBeforeNativeAccess(int offset, int limit)
    {
        var bridge = new FlInjectBridge();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.QueryNotesAsync(1, offset: offset, limit: limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.QueryClipsAsync(offset: offset, limit: limit));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => bridge.QueryPluginParametersAsync(0, offset: offset, limit: limit));
        Assert.Empty(commands);
    }

    [Fact]
    public async Task FullNotePageRespectsNativeFourKilobyteReadLimit()
    {
        ConfigureNotes(512);
        byte[] raw = Enumerable.Range(0, 512).SelectMany(i => Note(0, 60, i * 96)).ToArray();
        for (int offset = 0; offset < raw.Length; offset += 4096)
            replies[$"peekabs {0x30000 + offset:x} 4096"] = Convert.ToHexString(raw.AsSpan(offset, 4096));

        var page = await new FlInjectBridge().QueryNotesAsync(3);

        Assert.Equal(512, page.Items.Count);
        Assert.Equal(511 * 96, page.Items[^1].StartTick);
        Assert.Null(page.NextOffset);
        Assert.Equal(3, commands.Count(command => command.EndsWith(" 4096", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TruncatedNativeReadFailsWithUsefulError()
    {
        ConfigureNotes(1);
        replies["peekabs 30000 24"] = "00";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().QueryNotesAsync(3));
        Assert.Contains("incomplete response", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public async Task ImplausibleNoteCountsFailBeforeReadingData(int count)
    {
        ConfigureNotes(count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().QueryNotesAsync(3));
        Assert.DoesNotContain("peekabs 20008 8", commands);
    }

    [Fact]
    public async Task ClipQueryIncludesTemplateAndChannelZeroClipsWithoutSelectionFlag()
    {
        replies["call 11e32c0"] = "{\"ok\":1,\"ret\":\"0x10000\"}";
        replies["peekabs 10014 8"] = Hex(0x20000UL);
        replies["peekabs 20008 8"] = Hex(0x30000UL);
        replies["peekabs 20010 4"] = Hex(0x38);
        replies["peekabs 20014 4"] = Hex(2);
        replies["peekabs 30000 36"] = Convert.ToHexString(Clip(0, 500, false));
        replies["peekabs 30038 36"] = Convert.ToHexString(Clip(0x50030000, 498, true));

        var page = await new FlInjectBridge().QueryClipsAsync();

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(("channel", 0, 500), (page.Items[0].SourceKind, page.Items[0].SourceIndex, page.Items[0].Track));
        Assert.Equal(("pattern", 3, 498, true),
            (page.Items[1].SourceKind, page.Items[1].SourceIndex, page.Items[1].Track, page.Items[1].Muted));
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public async Task OffsetPastEndReturnsEmptyWithoutOverflowOrDataRead()
    {
        ConfigureNotes(1);
        var result = await new FlInjectBridge().QueryNotesAsync(3, offset: int.MaxValue);
        Assert.Empty(result.Items);
        Assert.Null(result.NextOffset);
        Assert.Equal(1, result.Total);
        Assert.DoesNotContain("peekabs 20008 8", commands);
    }

    private void ConfigureNotes(int count)
    {
        replies["peek 1803dd0 8"] = Hex(0x20000UL);
        replies["peekabs 20014 4"] = Hex(count);
        replies["peekabs 20008 8"] = Hex(0x30000UL);
    }

    private void AddString(ulong address, string value)
    {
        replies[$"peekabs {address - 4:x} 4"] = Hex(value.Length);
        replies[$"peekabs {address:x} {value.Length * 2}"] = Convert.ToHexString(Encoding.Unicode.GetBytes(value));
    }

    private static byte[] Note(ushort channel, ushort key, int start, bool muted = false)
    {
        byte[] raw = new byte[24];
        BitConverter.GetBytes(start).CopyTo(raw, 0);
        BitConverter.GetBytes(channel).CopyTo(raw, 6);
        BitConverter.GetBytes(96).CopyTo(raw, 8);
        BitConverter.GetBytes(key).CopyTo(raw, 12);
        raw[0x13] = muted ? (byte)0x20 : (byte)0;
        raw[0x15] = 100;
        return raw;
    }

    private static byte[] Clip(uint source, short track, bool muted)
    {
        byte[] raw = new byte[36];
        BitConverter.GetBytes(source).CopyTo(raw, 4);
        BitConverter.GetBytes(384).CopyTo(raw, 8);
        BitConverter.GetBytes((short)(500 - track)).CopyTo(raw, 12);
        raw[0x13] = muted ? (byte)0x20 : (byte)0;
        return raw;
    }

    private static string Hex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));
    private static string Hex(ulong value) => Convert.ToHexString(BitConverter.GetBytes(value));

    private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        commands.Add(command);
        return Task.FromResult(replies.TryGetValue(command, out string? reply)
            ? reply : throw new InvalidOperationException("Unexpected native access: " + command));
    }
}
