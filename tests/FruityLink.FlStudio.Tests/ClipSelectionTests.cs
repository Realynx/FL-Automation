using System.Globalization;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class ClipSelectionTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? original = FlInjectBridge.Transport;
    public void Dispose() => FlInjectBridge.Transport = original;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExistingClipReachesInsertionRegardlessOfSelection(bool selected, bool slice)
    {
        var model = new ClipTransport(selected);
        FlInjectBridge.Transport = model.SendAsync;
        var bridge = new FlInjectBridge();

        // Stop at the native insertion boundary: this regression concerns whether an
        // existing, unselected FL-authored clip is accepted, not the native allocator.
        await Assert.ThrowsAsync<InsertionReachedException>(() => slice
            ? bridge.SliceClipAsync(0, 192)
            : bridge.DuplicateClipAsync(0));

        Assert.Equal(slice ? 192 : 384, BitConverter.ToInt32(model.Clip, 8));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task RemovedOrOutOfRangeSlotStillFailsBeforeMutation(int index)
    {
        var model = new ClipTransport(false);
        FlInjectBridge.Transport = model.SendAsync;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().DuplicateClipAsync(index));

        Assert.Contains("out of range", error.Message);
        Assert.Equal(0, model.Writes);
    }

    private sealed class InsertionReachedException : Exception;

    private sealed class ClipTransport
    {
        public byte[] Clip { get; } = new byte[0x48];
        public int Writes { get; private set; }

        public ClipTransport(bool selected)
        {
            BitConverter.GetBytes(0x50000001U).CopyTo(Clip, 4);
            BitConverter.GetBytes(384).CopyTo(Clip, 8);
            BitConverter.GetBytes((short)499).CopyTo(Clip, 12);
            Clip[0x13] = selected ? (byte)0x80 : (byte)0;
        }

        public Task<string> SendAsync(string message, int _, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string[] parts = message.Split(' ');
            if (message == "call 11e32c0") return Task.FromResult("{\"ok\":1,\"ret\":\"0x10000\"}");
            if (parts[0] == "scratch") throw new InsertionReachedException();
            if (parts[0] == "peekabs") return Task.FromResult(Convert.ToHexString(Read(parts)));
            if (parts[0] != "pokeabs" || parts[1] != "30008") throw new InvalidOperationException(message);
            Convert.FromHexString(parts[2]).CopyTo(Clip, 8);
            Writes++;
            return Task.FromResult("{\"ok\":1}");
        }

        private byte[] Read(string[] parts) => parts[1] switch
        {
            "10014" => BitConverter.GetBytes(0x20000UL),
            "20008" => BitConverter.GetBytes(0x30000UL),
            "20010" => BitConverter.GetBytes(0x48),
            "20014" => BitConverter.GetBytes(1),
            "30000" => Clip[..int.Parse(parts[2], CultureInfo.InvariantCulture)],
            _ => throw new InvalidOperationException(string.Join(' ', parts)),
        };
    }
}
