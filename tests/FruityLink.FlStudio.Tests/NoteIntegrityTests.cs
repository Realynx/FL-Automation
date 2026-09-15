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
    public async Task EditOfStackedDuplicatesIsRefusedBeforeAnyWrite()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 96, 100), new NoteSpec(1, 60, 0, 192, 100), new NoteSpec(1, 64, 96, 96, 100));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().EditNotesAsync(1,
            [new(1, 64, 96, NewVelocity: 50), new(1, 60, 0, NewVelocity: 90)]));
        Assert.Contains("matches 2 stacked notes (lengths 96, 192)", error.Message);
        Assert.Contains("lengthTick", error.Message);
        Assert.DoesNotContain(native.Commands, IsMutation);
        Assert.All(native.Records, record => Assert.Equal(100, record[21]));
    }

    [Fact]
    public async Task LengthTickPicksOneStackedDuplicateAndAllowMultipleEditsAll()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 96, 100), new NoteSpec(1, 60, 0, 192, 100));
        Assert.Equal(1, await new FlInjectBridge().EditNotesAsync(1, [new(1, 60, 0, NewVelocity: 90, LengthTick: 192)]));
        Assert.Equal(100, native.Records.Single(r => BitConverter.ToInt32(r, 8) == 96)[21]);
        Assert.Equal(90, native.Records.Single(r => BitConverter.ToInt32(r, 8) == 192)[21]);
        Assert.Equal(0, await new FlInjectBridge().EditNotesAsync(1, [new(1, 60, 0, NewVelocity: 70, LengthTick: 48)]));
        Assert.Equal(2, await new FlInjectBridge().EditNotesAsync(1, [new(1, 60, 0, NewVelocity: 70)], allowMultiple: true));
        Assert.All(native.Records, record => Assert.Equal(70, record[21]));
    }

    [Fact]
    public async Task DeleteRefusesAmbiguousTargetsAndHonorsLengthTickAndAllowMultiple()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 96, 100), new NoteSpec(1, 60, 0, 96, 100), new NoteSpec(1, 60, 0, 192, 100), new NoteSpec(1, 64, 96, 96, 100));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().DeleteNotesAsync(1, [new(1, 60, 0)]));
        Assert.DoesNotContain(native.Commands, IsMutation);
        Assert.Equal(4, native.Records.Count);
        Assert.Equal(1, await new FlInjectBridge().DeleteNotesAsync(1, [new(1, 60, 0, 192)]));
        Assert.Equal(3, native.Records.Count);
        // Identical duplicates (same length) can only be addressed together.
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().DeleteNotesAsync(1, [new(1, 60, 0, 96)]));
        Assert.Equal(2, await new FlInjectBridge().DeleteNotesAsync(1, [new(1, 60, 0, 96)], allowMultiple: true));
        var survivor = Assert.Single(native.Records);
        Assert.Equal(64, BitConverter.ToUInt16(survivor, 0xC));
    }

    private const uint Pattern1Clip = 0x50005000u + (1u << 16), Pattern2Clip = 0x50005000u + (2u << 16);

    [Fact]
    public async Task DeleteRestoresPlacedClipLengthsThatThePatternRebuildShrank()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 1536, 100), new NoteSpec(1, 60, 1536, 1536, 100));
        native.SeedClips((Pattern1Clip, 0, 490, 3072), (Pattern2Clip, 3072, 488, 768), (Pattern1Clip, 6144, 490, 3072));
        // The live host re-derives every clip of the edited pattern from its remaining notes.
        native.OnRebuild = () => { native.SetClipLength(0, 1536); native.SetClipLength(2, 1536); };

        Assert.Equal(1, await new FlInjectBridge().DeleteNotesAsync(1, [new(1, 60, 1536)]));

        Assert.Single(native.Records);
        Assert.Equal(3072, native.ClipLength(0));
        Assert.Equal(3072, native.ClipLength(2));
        Assert.Equal(768, native.ClipLength(1));
        string bits3072 = BitConverter.DoubleToInt64Bits(3072).ToString("x", CultureInfo.InvariantCulture);
        Assert.Equal([$"callfabs 60100 90000 0 {bits3072}", $"callfabs 60100 90070 0 {bits3072}"],
            native.Commands.Where(command => command.StartsWith("callfabs", StringComparison.Ordinal)).ToArray());
        Assert.True(native.Commands.IndexOf("call 11d4140 1 1") < native.Commands.FindIndex(c => c.StartsWith("callfabs", StringComparison.Ordinal)),
            "clip lengths are re-pinned after the rebuild that shrank them");
    }

    [Fact]
    public async Task EditLeavesClipsAloneWhenTheRebuildKeepsTheirLengths()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 1536, 100), new NoteSpec(1, 60, 1536, 1536, 100));
        native.SeedClips((Pattern1Clip, 0, 490, 3072), (Pattern2Clip, 3072, 488, 768));

        Assert.Equal(1, await new FlInjectBridge().EditNotesAsync(1, [new(1, 60, 1536, NewVelocity: 90)]));

        Assert.Equal(3072, native.ClipLength(0));
        Assert.DoesNotContain(native.Commands, command => command.StartsWith("callfabs", StringComparison.Ordinal));
        Assert.DoesNotContain(native.Commands, command => command.StartsWith("resolve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeleteWithoutAPlaylistStillWritesNotes()
    {
        using var native = new NoteNative();
        native.Seed(new NoteSpec(1, 60, 0, 96, 100), new NoteSpec(1, 64, 96, 96, 100));

        Assert.Equal(1, await new FlInjectBridge().DeleteNotesAsync(1, [new(1, 64, 96)]));

        Assert.Single(native.Records);
        Assert.Contains("call 11e32c0", native.Commands);
        Assert.DoesNotContain(native.Commands, command => command.StartsWith("callfabs", StringComparison.Ordinal));
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

        /// <summary>Seed a playlist clip collection (stride 0x38 at <see cref="ClipData"/>) so note edits can see
        /// placed clips; each entry is (source id, start tick, track field, length).</summary>
        public void SeedClips(params (uint Source, int Start, short TrackField, int Length)[] clips)
        {
            Arrangement = 0x70000;
            Write(0x70014, BitConverter.GetBytes(0x80000UL));
            Write(0x80008, BitConverter.GetBytes(ClipData));
            Write(0x80010, BitConverter.GetBytes(ClipStride));
            Write(0x80014, BitConverter.GetBytes(clips.Length));
            Write(0x149e8b4, BitConverter.GetBytes(-1));   // no current arrangement: RecomputeSongLength is a no-op
            for (int i = 0; i < clips.Length; i++)
            {
                ulong clip = ClipData + (ulong)i * (ulong)ClipStride;
                Write(clip, BitConverter.GetBytes(clips[i].Start));
                Write(clip + 4, BitConverter.GetBytes(clips[i].Source));
                Write(clip + 8, BitConverter.GetBytes(clips[i].Length));
                Write(clip + 0xc, BitConverter.GetBytes(clips[i].TrackField));
            }
        }

        public const ulong ClipData = 0x90000;
        public const int ClipStride = 0x38;
        public ulong Arrangement { get; private set; }
        /// <summary>Runs on FL's pattern rebuild (11d4140), where the live host re-derives clip lengths.</summary>
        public Action? OnRebuild { get; set; }
        public int ClipLength(int index) => BitConverter.ToInt32(Read(ClipData + (ulong)index * (ulong)ClipStride + 8, 4));
        public void SetClipLength(int index, int length) => Write(ClipData + (ulong)index * (ulong)ClipStride + 8, BitConverter.GetBytes(length));

        private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Commands.Add(command);
            Write(0x20010, BitConverter.GetBytes(ChannelCount));
            string[] parts = command.Split(' ');
            if (parts[0] == "resolve") return Task.FromResult(parts[1] == "sym:FLpl_SetClipSourceRange" ? "60100" : "0");
            if (parts[0] == "callfabs") return Task.FromResult("{\"ok\":1,\"ret\":\"0x0\",\"xmm0\":\"0x0\"}");
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
            if (parts[1] == "11e32c0") return $"{{\"ok\":1,\"ret\":\"0x{Arrangement:x}\"}}";   // FLpl_GetCurrentArrangement
            if (parts[1] == "11d4140") OnRebuild?.Invoke();
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
