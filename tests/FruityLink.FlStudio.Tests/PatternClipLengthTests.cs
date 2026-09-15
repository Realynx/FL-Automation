using System.Globalization;
using System.Reflection;
using FruityLink.Core.Abstractions;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

/// <summary>AddPatternClips honours an explicit PatternClipSpec.LengthTick by pinning the clip's source range
/// (Parking Lot Moon 2026-09-14: 8-bar specs came back at the pattern's 9-10 bar content length).</summary>
public sealed class PatternClipLengthTests
{
    [Fact]
    public async Task ExplicitLengthIsPinnedThroughTheSourceRangeBeforeThePatternRefresh()
    {
        using var native = new PlaylistNative { PatternLength = 3840 };

        await new FlInjectBridge().AddPatternClipsAsync([new PatternClipSpec(1, 9, 0, 3072)]);

        Assert.Equal(1, native.ClipCount);
        Assert.Equal(3072, native.ClipLength(0));
        Assert.Equal(0x50015000u, native.ClipSource(0));
        string bits = BitConverter.DoubleToInt64Bits(3072).ToString("x", CultureInfo.InvariantCulture);
        Assert.Contains($"callfabs 60100 {PlaylistNative.ClipData:x} 0 {bits}", native.Commands);
        int pin = native.Commands.FindIndex(c => c.StartsWith("callfabs", StringComparison.Ordinal));
        int refresh = native.Commands.IndexOf("call 11d4140 1 1");
        Assert.True(pin >= 0 && refresh > pin, "the length is pinned before FL's pattern refresh re-derives clip lengths");
        Assert.DoesNotContain("peek 14aa0c8 8", native.Commands);
    }

    [Fact]
    public async Task ZeroLengthTakesThePatternLengthWithoutResolvingTheRangeSetter()
    {
        using var native = new PlaylistNative { PatternLength = 3840 };

        await new FlInjectBridge().AddPatternClipsAsync([new PatternClipSpec(1, 9, 768, 0)]);

        Assert.Equal(3840, native.ClipLength(0));
        Assert.DoesNotContain(native.Commands, c => c.StartsWith("callfabs", StringComparison.Ordinal));
        Assert.DoesNotContain(native.Commands, c => c.StartsWith("resolve", StringComparison.Ordinal));
    }

    /// <summary>Byte-level model of the playlist clip collection plus the scratch/vtable insert path.</summary>
    private sealed class PlaylistNative : IDisposable
    {
        public const ulong ClipData = 0x90000;
        private const ulong Arrangement = 0x70000, Collection = 0x80000, Vtable = 0xa0000, InitFn = 0xb0000, InsertFn = 0xb0010,
            Scratch = 0xc0000, PatternArray = 0xd0000;
        private const int Stride = 0x38;
        private readonly Func<string, int, CancellationToken, Task<string>>? originalTransport = FlInjectBridge.Transport;
        private readonly Dictionary<ulong, byte> memory = [];
        private readonly FieldInfo moduleBase = typeof(FlInjectBridge).GetField("_modBase", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo moduleEnd = typeof(FlInjectBridge).GetField("_modEnd", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? originalBase;
        private readonly object? originalEnd;
        public List<string> Commands { get; } = [];
        public int PatternLength { get; init; }
        public int ClipCount => BitConverter.ToInt32(Read(Collection + 0x14, 4));

        public PlaylistNative()
        {
            originalBase = moduleBase.GetValue(null); originalEnd = moduleEnd.GetValue(null);
            moduleBase.SetValue(null, InitFn); moduleEnd.SetValue(null, InitFn + 0x1000);
            Write(Arrangement + 0x14, BitConverter.GetBytes(Collection));
            Write(Collection, BitConverter.GetBytes(Vtable));
            Write(Collection + 8, BitConverter.GetBytes(ClipData));
            Write(Collection + 0x10, BitConverter.GetBytes(Stride));
            Write(Collection + 0x14, BitConverter.GetBytes(0));
            Write(Vtable + 8, BitConverter.GetBytes(InsertFn));
            Write(Vtable + 0x10, BitConverter.GetBytes(InitFn));
            Write(0x14aa0c8, BitConverter.GetBytes(PatternArray));
            Write(0x149e8b4, BitConverter.GetBytes(-1));   // no current arrangement: RecomputeSongLength is a no-op
            FlInjectBridge.Transport = SendAsync;
        }

        public void Dispose()
        {
            FlInjectBridge.Transport = originalTransport;
            moduleBase.SetValue(null, originalBase); moduleEnd.SetValue(null, originalEnd);
        }

        public int ClipLength(int index) => BitConverter.ToInt32(Read(ClipData + (ulong)index * Stride + 8, 4));
        public uint ClipSource(int index) => BitConverter.ToUInt32(Read(ClipData + (ulong)index * Stride + 4, 4));

        private Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Commands.Add(command);
            Write(PatternArray + 0xC0 + 0x50, BitConverter.GetBytes(PatternLength));
            string[] parts = command.Split(' ');
            string result = parts[0] switch
            {
                "scratch" => $"0x{Scratch:x}",
                "resolve" => parts[1] == "sym:FLpl_SetClipSourceRange" ? "60100" : "0",
                "callfabs" => "{\"ok\":1,\"ret\":\"0x0\",\"xmm0\":\"0x0\"}",
                "peek" or "peekabs" => Convert.ToHexString(Read(Hex(parts[1]), int.Parse(parts[2], CultureInfo.InvariantCulture))),
                "pokeabs" => Poke(Hex(parts[1]), Convert.FromHexString(parts[2])),
                "callabs" => CallAbs(Hex(parts[1]), parts),
                "call" => Call(parts),
                _ => throw new InvalidOperationException("Unexpected native operation: " + command),
            };
            return Task.FromResult(result);
        }

        private string Call(string[] parts)
        {
            if (parts[1] == "11e32c0") return $"{{\"ok\":1,\"ret\":\"0x{Arrangement:x}\"}}";
            Assert.Contains(parts[1], new[] { "11d4080", "11d4000", "f6e180", "11d4140", "f53d30", "d421c0", "107ead0" });
            return Ok;
        }

        private string CallAbs(ulong function, string[] parts)
        {
            Assert.Equal(Collection, Hex(parts[2]));
            if (function == InitFn) return Ok;
            Assert.Equal(InsertFn, function);
            int index = ClipCount;
            Write(ClipData + (ulong)index * Stride, Read(Hex(parts[3]), 0x48));
            Write(Collection + 0x14, BitConverter.GetBytes(index + 1));
            return $"{{\"ok\":1,\"ret\":\"0x{index:x}\"}}";
        }

        private string Poke(ulong address, byte[] bytes) { Write(address, bytes); return Ok; }
        private void Write(ulong address, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) memory[address + (ulong)i] = bytes[i]; }
        private byte[] Read(ulong address, int length) => Enumerable.Range(0, length).Select(index => memory.GetValueOrDefault(address + (ulong)index)).ToArray();
        private static ulong Hex(string value) => ulong.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        private const string Ok = "{\"ok\":1,\"ret\":\"0x0\"}";
    }
}
