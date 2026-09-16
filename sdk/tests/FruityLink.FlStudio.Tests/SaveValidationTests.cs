using System.Globalization;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class SaveValidationTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? _original = FlInjectBridge.Transport;
    private readonly Memory _memory = new();

    public SaveValidationTests() => FlInjectBridge.Transport = _memory.SendAsync;
    public void Dispose() => FlInjectBridge.Transport = _original;

    [Theory]
    [InlineData("save")]
    [InlineData("save_as")]
    [InlineData("copy")]
    [InlineData("version")]
    public async Task EverySaveRefusesOrphanBeforeAnyMutationOrPathChange(string operation)
    {
        byte[] notes = _memory.SetNotes(999, 4, 7);
        byte[] original = (byte[])notes.Clone();
        var bridge = new FlInjectBridge();
        Task Save() => operation switch
        {
            "save" => bridge.SaveProjectAsync("project.flp"),
            "save_as" => bridge.SaveProjectAsAsync("changed.flp"),
            "version" => bridge.SaveNewVersionAsync(),
            _ => bridge.SaveCopyAsync("copy.flp"),
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(Save);
        Assert.Contains("pattern 999, note 1", error.Message);
        Assert.Contains("missing channel 7", error.Message);
        Assert.Contains("project has 7 channels", error.Message);
        Assert.DoesNotContain(_memory.Commands, command => command == "scratch" || command.StartsWith("poke", StringComparison.Ordinal) || command.StartsWith("call", StringComparison.Ordinal));
        Assert.Equal(original, notes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task AllPatternSlotsIncludingReservedZeroAreInspected(int pattern)
    {
        _memory.SetNotes(pattern, ushort.MaxValue);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp"));
        Assert.Contains($"pattern {pattern}, note 0", error.Message);
        Assert.Contains("missing channel 65535", error.Message);
    }

    [Fact]
    public async Task OrphanInSecondChunkIsReportedUsingAbsoluteNoteIndex()
    {
        ushort[] channels = Enumerable.Repeat((ushort)6, 172).ToArray();
        channels[171] = 7;
        _memory.SetNotes(3, channels);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp"));
        Assert.Contains("pattern 3, note 171", error.Message);
        Assert.All(_memory.ReadSizes, size => Assert.InRange(size, 1, 4096));
    }

    [Fact]
    public async Task ValidProjectReachesSaveWithoutPerEmptyPatternCalls()
    {
        _memory.SetNotes(12, 0, 6);
        await Assert.ThrowsAsync<SaveReachedException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp"));
        Assert.Equal(58, _memory.Commands.Count); // 54 fixed reads/resolution + 2 headers + 1 note batch + scratch.
        Assert.DoesNotContain(_memory.Commands, command => command.StartsWith("call ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyProjectWithZeroChannelsCanSave()
    {
        _memory.ChannelCount = 0;
        await Assert.ThrowsAsync<SaveReachedException>(() => new FlInjectBridge().SaveCopyAsync("empty.flp"));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(1_000_001, false)]
    [InlineData(1, true)]
    public async Task InvalidRecorderMetadataBlocksSave(int count, bool missingData)
    {
        _memory.SetNotes(2, 0);
        _memory.OverrideHeader(2, count, missingData);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp"));
        Assert.Contains("pattern 2 has an unreadable note store", error.Message);
        Assert.DoesNotContain("scratch", _memory.Commands);
    }

    [Fact]
    public async Task CancellationDuringScanDoesNotEnterWriter()
    {
        using var cancellation = new CancellationTokenSource();
        _memory.OnRead = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp", cancellation.Token));
        Assert.DoesNotContain("scratch", _memory.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentCollectionChangesBlockSave(bool noteCountChanges)
    {
        _memory.SetNotes(0, 0, 1);
        _memory.OnRead = () =>
        {
            if (!_memory.Commands[^1].StartsWith("peekabs 400000 ", StringComparison.Ordinal)) return;
            if (noteCountChanges) _memory.OverrideHeader(0, 1, false);
            else _memory.ChannelCount = 6;
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp"));
        Assert.Contains("changed during", error.Message);
        Assert.DoesNotContain("scratch", _memory.Commands);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65537)]
    public async Task ImplausibleChannelCountBlocksSave(int count)
    {
        _memory.ChannelCount = count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp"));
        Assert.Contains("invalid channel count", error.Message);
        Assert.DoesNotContain("scratch", _memory.Commands);
    }

    [Fact]
    public async Task UnreadableRecorderBlocksSaveInsteadOfSkippingIt()
    {
        _memory.SetNotes(7, 0);
        _memory.OnRead = () =>
        {
            if (_memory.Commands[^1].StartsWith("peekabs 2000e0 ", StringComparison.Ordinal))
                throw new InvalidOperationException("Native memory read failed.");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SaveCopyAsync("copy.flp"));
        Assert.DoesNotContain("scratch", _memory.Commands);
    }

    private sealed class SaveReachedException : Exception;

    private sealed class Memory
    {
        private const ulong Table = 0x100000, Headers = 0x200000, Notes = 0x400000;
        private readonly byte[] _table = new byte[1000 * 0xC0];
        private readonly Dictionary<ulong, byte[]> _regions = new();
        internal int ChannelCount { get; set; } = 7;
        internal Action? OnRead { get; set; }
        internal List<string> Commands { get; } = [];
        internal List<int> ReadSizes { get; } = [];

        internal byte[] SetNotes(int pattern, params ushort[] channels)
        {
            ulong recorder = Headers + (ulong)pattern * 0x20;
            ulong data = Notes + (ulong)pattern * 0x10000;
            BitConverter.GetBytes(recorder).CopyTo(_table, pattern * 0xC0);
            var header = new byte[0x18];
            BitConverter.GetBytes(data).CopyTo(header, 8);
            BitConverter.GetBytes(channels.Length).CopyTo(header, 0x14);
            _regions[recorder] = header;
            var notes = Enumerable.Repeat((byte)0xAD, channels.Length * 24).ToArray();
            for (int i = 0; i < channels.Length; i++) BitConverter.GetBytes(channels[i]).CopyTo(notes, i * 24 + 6);
            _regions[data] = notes;
            return notes;
        }

        internal void OverrideHeader(int pattern, int count, bool missingData)
        {
            byte[] header = _regions[Headers + (ulong)pattern * 0x20];
            BitConverter.GetBytes(count).CopyTo(header, 0x14);
            if (missingData) Array.Clear(header, 8, 8);
        }

        internal Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Commands.Add(command);
            string[] parts = command.Split(' ');
            if (command == "resolve sym:NoteRecorderArrayBase") return Task.FromResult(Table.ToString("x", CultureInfo.InvariantCulture));
            if (command == "peek 14a98d8 8") return Hex(BitConverter.GetBytes(0x8000UL));
            if (command == "scratch") throw new SaveReachedException();
            if (parts[0] != "peekabs") throw new InvalidOperationException("Unexpected mutation/command: " + command);
            ulong address = ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int count = int.Parse(parts[2], CultureInfo.InvariantCulture);
            ReadSizes.Add(count);
            OnRead?.Invoke();
            return Hex(Read(address, count));
        }

        private byte[] Read(ulong address, int count)
        {
            if (address == 0x8000) return BitConverter.GetBytes(0x9000UL);
            if (address == 0x9010) return BitConverter.GetBytes(ChannelCount);
            if (address >= Table && address + (ulong)count <= Table + (ulong)_table.Length)
                return _table.AsSpan((int)(address - Table), count).ToArray();
            foreach (var region in _regions)
                if (address >= region.Key && address + (ulong)count <= region.Key + (ulong)region.Value.Length)
                    return region.Value.AsSpan((int)(address - region.Key), count).ToArray();
            throw new InvalidOperationException("Unreadable fixture address.");
        }

        private static Task<string> Hex(byte[] bytes) => Task.FromResult(Convert.ToHexString(bytes));
    }
}
