using System.Globalization;
using System.Reflection;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class NoteIntegrityTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(12)]
    [InlineData(65535)]
    public async Task MissingChannelRejectsEntireBatchBeforeRecorderCreation(int channel)
    {
        using var native = new NoteNative();
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().AddNotesAsync(1,
            [new(1, 60, 0, 96, 100), new(channel, 62, 96, 96, 100)]));
        Assert.Contains("does not exist", error.Message);
        Assert.DoesNotContain(native.Commands, IsMutation);
    }

    [Theory]
    [InlineData(-1, 60, 0, 96, 100)]
    [InlineData(65536, 60, 0, 96, 100)]
    [InlineData(0, -1, 0, 96, 100)]
    [InlineData(0, 132, 0, 96, 100)]
    [InlineData(0, 60, -1, 96, 100)]
    [InlineData(0, 60, 0, 0, 100)]
    [InlineData(0, 60, 0, -1, 100)]
    [InlineData(0, 60, int.MaxValue, 1, 100)]
    [InlineData(0, 60, 1, int.MaxValue, 100)]
    [InlineData(0, 60, 0, 96, -1)]
    [InlineData(0, 60, 0, 96, 128)]
    public async Task InvalidScalarRejectsBeforeNativeAccess(int channel, int key, int start, int length, int velocity)
    {
        using var native = new NoteNative();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().AddNotesAsync(1,
            [new(1, 60, 0, 96, 100), new(channel, key, start, length, velocity)]));
        Assert.Empty(native.Commands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65537)]
    public async Task UnavailableChannelCountRejectsBeforeMutation(int count)
    {
        using var native = new NoteNative { ChannelCount = count };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().AddNoteAsync(1, 0, 60, 0, 96, 100));
        Assert.DoesNotContain(native.Commands, IsMutation);
    }

    [Fact]
    public async Task AppendWritesCompleteDurationsAndCanonicalDefaultsWithoutNoteOff()
    {
        using var native = new NoteNative();
        await new FlInjectBridge().AddNotesAsync(1, [new(1, 131, int.MaxValue - 1, 1, 127), new(0, 0, 0, 96, 0)]);
        Assert.Equal(2, native.Records.Count);
        Assert.Equal(0, BitConverter.ToInt32(native.Records[0], 0));
        Assert.Equal(int.MaxValue - 1, BitConverter.ToInt32(native.Records[1], 0));
        Assert.Equal(96, BitConverter.ToInt32(native.Records[0], 8));
        Assert.Equal(1, BitConverter.ToInt32(native.Records[1], 8));
        Assert.Equal(new byte[] { 120, 0, 64, 0, 64, 127, 128, 128 }, native.Records[1][16..]);
        Assert.Equal((ushort)0x4000, BitConverter.ToUInt16(native.Records[1], 4));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(native.Records[1], 6));
        Assert.DoesNotContain(native.Commands, command => command.StartsWith("call f6d880", StringComparison.Ordinal));
        Assert.Equal(1, native.CommitCount);
    }

    [Fact]
    public async Task CancellationAfterAppendStopsBeforeNextNoteButCommitsCompletePrefix()
    {
        using var native = new NoteNative();
        using var cancellation = new CancellationTokenSource();
        native.AfterAppend = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FlInjectBridge().AddNotesAsync(1,
            [new(1, 60, 96, 48, 100), new(1, 64, 0, 48, 100)], cancellation.Token));
        Assert.Single(native.Records);
        Assert.Equal(48, BitConverter.ToInt32(native.Records[0], 8));
        Assert.Equal(1, native.CommitCount);
        Assert.Contains("call 107ead0", native.Commands);
    }

    [Fact]
    public async Task NativeFailureNeverRetriesAppendAndStillFinalizesRecorder()
    {
        using var native = new NoteNative { FailAppend = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().AddNoteAsync(1, 1, 60, 0, 96, 100));
        Assert.Single(native.Commands, command => command.StartsWith("call f6d740", StringComparison.Ordinal));
        Assert.Equal(1, native.CommitCount);
    }

    [Fact]
    public async Task InvalidEditAfterValidEditRejectsBeforeAnyWrite()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 96, 100), new NoteSpec(1, 64, 96, 96, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().EditNotesAsync(1,
            [new(1, 60, 0, NewVelocity: 90), new(1, 64, 96, NewStartTick: int.MaxValue)]));
        Assert.DoesNotContain(native.Commands, IsMutation);
        Assert.Equal(100, native.Records[0][21]);
    }

    [Fact]
    public async Task ValidEditPreservesEveryUnspecifiedByte()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 96, 100));
        native.Records[0][14] = 13; // Native group and expression metadata are not reconstructed.
        native.Records[0][17] = 7;
        native.Records[0][20] = 91;
        native.SyncRecords();
        byte[] expected = native.Records[0].ToArray();
        expected[21] = 88;
        Assert.Equal(1, await new FlInjectBridge().EditNotesAsync(1, [new(1, 60, 0, NewVelocity: 88)]));
        Assert.Equal(expected, native.Records[0]);
    }

    [Fact]
    public async Task MuteEditPreservesNativeVelocityOutsidePublicAuthoringRange()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 96, 128));
        byte[] expected = native.Records[0].ToArray();
        expected[19] |= 0x20;
        Assert.Equal(1, await new FlInjectBridge().EditNotesAsync(1, [new(1, 60, 0, Muted: true)]));
        Assert.Equal(expected, native.Records[0]);
    }

    [Fact]
    public async Task CloneOfOrphanNotesRejectsBeforeSelectingOrCreatingPattern()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(12, 60, 0, 96, 100));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().ClonePatternAsync(1));
        Assert.DoesNotContain(native.Commands, IsMutation);
    }

    private static bool IsMutation(string command) => command.StartsWith("call", StringComparison.Ordinal) || command.StartsWith("poke", StringComparison.Ordinal);

    private sealed class NoteNative : IDisposable
    {
        private const ulong Recorder = 0x30000, Data = 0x40000, Vtable = 0x50000, Commit = 0x60000;
        private readonly Func<string, int, CancellationToken, Task<string>>? originalTransport = FlInjectBridge.Transport;
        private readonly Dictionary<ulong, byte> memory = [];
        private readonly FieldInfo moduleBase = typeof(FlInjectBridge).GetField("_modBase", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo moduleEnd = typeof(FlInjectBridge).GetField("_modEnd", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? originalBase;
        private readonly object? originalEnd;
        public List<string> Commands { get; } = [];
        public List<byte[]> Records { get; } = [];
        public int ChannelCount { get; init; } = 2;
        public int CommitCount { get; private set; }
        public bool FailAppend { get; init; }
        public Action? AfterAppend { get; set; }

        public NoteNative()
        {
            originalBase = moduleBase.GetValue(null); originalEnd = moduleEnd.GetValue(null);
            moduleBase.SetValue(null, Commit); moduleEnd.SetValue(null, Commit + 0x1000);
            Write(0x14a98d8, BitConverter.GetBytes(0x10000UL));
            Write(0x10000, BitConverter.GetBytes(0x20000UL));
            Write(0x1803c50, BitConverter.GetBytes(Recorder));
            Write(Recorder, BitConverter.GetBytes(Vtable));
            Write(Recorder + 8, BitConverter.GetBytes(Data));
            Write(Vtable + 0x18, BitConverter.GetBytes(Commit));
            FlInjectBridge.Transport = SendAsync;
        }

        public void Dispose()
        {
            FlInjectBridge.Transport = originalTransport;
            moduleBase.SetValue(null, originalBase); moduleEnd.SetValue(null, originalEnd);
        }

        public void Seed(params NoteSpec[] notes)
        {
            foreach (var note in notes) Records.Add(Encode([Recorder, (uint)note.StartTick,
                ((uint)note.Channel << 16) | 0x4000u, (uint)note.LengthTick, (uint)note.Key,
                0x00400078, 0x80800040u | ((uint)note.Velocity << 8)]));
            SyncRecords();
        }

        public void SyncRecords()
        {
            Write(Recorder + 0x14, BitConverter.GetBytes(Records.Count));
            for (int i = 0; i < Records.Count; i++) Write(Data + (ulong)i * 24, Records[i]);
        }

        private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Commands.Add(command);
            Write(0x20010, BitConverter.GetBytes(ChannelCount));
            string[] parts = command.Split(' ');
            ulong address = ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string result = parts[0] switch
            {
                "peek" or "peekabs" => Convert.ToHexString(Read(address, int.Parse(parts[2], CultureInfo.InvariantCulture))),
                "pokeabs" => Poke(address, Convert.FromHexString(parts[2])),
                "callabs" => CommitNotes(address),
                "call" => Call(parts),
                _ => throw new InvalidOperationException("Unexpected native operation: " + command),
            };
            return Task.FromResult(result);
        }

        private string Call(string[] parts)
        {
            if (parts[1] == "11d4080") return "{\"ok\":1,\"ret\":\"0x30000\"}";
            if (parts[1] == "f6d740")
            {
                if (FailAppend) return "{\"ok\":0,\"ret\":\"0x0\"}";
                Records.Add(Encode(parts.Skip(2).Select(value => ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray()));
                SyncRecords(); AfterAppend?.Invoke();
            }
            else Assert.Contains(parts[1], new[] { "11d4140", "f53d30", "d421c0", "107ead0" });
            return Success;
        }

        private string CommitNotes(ulong address)
        {
            Assert.Equal(Commit, address); CommitCount++;
            int count = BitConverter.ToInt32(Read(Recorder + 0x14, 4));
            Records.Clear();
            for (int i = 0; i < count; i++) Records.Add(Read(Data + (ulong)i * 24, 24));
            Records.Sort((a, b) => BitConverter.ToInt32(a).CompareTo(BitConverter.ToInt32(b)));
            SyncRecords(); return Success;
        }

        private string Poke(ulong address, byte[] bytes) { Write(address, bytes); return Success; }
        private void Write(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) memory[address + (ulong)i] = bytes[i]; }
        private byte[] Read(ulong address, int length) => Enumerable.Range(0, length).Select(index => memory.GetValueOrDefault(address + (ulong)index)).ToArray();
        private const string Success = "{\"ok\":1,\"ret\":\"0x0\"}";

        private static byte[] Encode(ulong[] args)
        {
            Assert.Equal(7, args.Length);
            byte[] note = new byte[24];
            BitConverter.GetBytes((uint)args[1]).CopyTo(note, 0);
            BitConverter.GetBytes((uint)args[2]).CopyTo(note, 4);
            BitConverter.GetBytes((uint)args[3]).CopyTo(note, 8);
            BitConverter.GetBytes((ushort)args[4]).CopyTo(note, 12);
            BitConverter.GetBytes((uint)args[5]).CopyTo(note, 16);
            BitConverter.GetBytes((uint)args[6]).CopyTo(note, 20);
            return note;
        }
    }
}
