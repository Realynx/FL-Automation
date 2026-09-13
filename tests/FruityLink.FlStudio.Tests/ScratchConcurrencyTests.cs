using System.Globalization;
using System.Text;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class ScratchConcurrencyTests : IDisposable
{
    private readonly Func<string, int, CancellationToken, Task<string>>? _original = FlInjectBridge.Transport;
    private readonly ScratchTransport _transport = new();

    public ScratchConcurrencyTests() => FlInjectBridge.Transport = _transport.SendAsync;

    public void Dispose() => FlInjectBridge.Transport = _original;

    [Fact]
    public async Task DifferentBridgeInstancesKeepTheirRenameArguments()
    {
        _transport.PauseWhen = command => command.StartsWith("pokeabs ", StringComparison.Ordinal);
        Task first = new FlInjectBridge().SetPatternNameAsync(1, "Verse");
        await _transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task second = new FlInjectBridge().RenameArrangementAsync(2, "Chorus");
        try { Assert.Equal(1, _transport.ScratchRequests); }
        finally { _transport.Release.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Verse", _transport.Names[1]);
        Assert.Equal("Chorus", _transport.Names[2]);
    }

    [Fact]
    public async Task ConcurrentOutParameterReadsKeepTheirOwnResults()
    {
        _transport.PauseWhen = command => command.StartsWith("call 11fb160 ", StringComparison.Ordinal);
        Task<string> first = new FlInjectBridge().GetArrangementNameAsync(1);
        await _transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<string> second = new FlInjectBridge().GetArrangementNameAsync(2);
        _transport.Release.TrySetResult();
        string[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["Arrangement 1", "Arrangement 2"], results);
    }

    [Fact]
    public async Task CancellationDuringNativeWorkReleasesScratchAfterWorkCompletes()
    {
        using var cancellation = new CancellationTokenSource();
        FlInjectBridge.Transport = (command, timeout, ct) => InProcBridge.RawAsync(command, timeout, ct, _transport.RunNative);
        _transport.PauseWhen = command => command.StartsWith("call 11d3960 ", StringComparison.Ordinal);
        Task first = new FlInjectBridge().SetPatternNameAsync(1, "Verse", cancellation.Token);
        await _transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Task? second = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            second = new FlInjectBridge().SetPatternNameAsync(2, "Chorus");
            Assert.False(second.IsCompleted);
            Assert.Equal(1, _transport.ScratchRequests);
            // The abandoned native call blocks scratch reuse, without globally blocking native I/O.
            Assert.Equal("pong", await new FlInjectBridge().RawAsync("ping").WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { _transport.Release.TrySetResult(); }
        await second!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Verse", _transport.Names[1]); // native call consumed the original argument after cancellation
        Assert.Equal("Chorus", _transport.Names[2]);
    }

    [Theory]
    [InlineData("scratch", false)]
    [InlineData("scratch", true)]
    [InlineData("call 11d3960 ", false)]
    [InlineData("call 11d3960 ", true)]
    public async Task NativeTimeoutRetainsScratchUntilActualCompletion(string pausePrefix, bool nativeFault)
    {
        _transport.PauseWhen = command => command.StartsWith(pausePrefix, StringComparison.Ordinal);
        _transport.FailPausedWork = nativeFault;
        FlInjectBridge.Transport = (command, timeout, ct) => InProcBridge.RawAsync(command,
            command.StartsWith(pausePrefix, StringComparison.Ordinal) ? 250 : timeout, ct, _transport.RunNative);
        Task first = new FlInjectBridge().SetPatternNameAsync(1, "Verse");
        await _transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task? second = null;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            int resets = _transport.ScratchRequests;
            second = new FlInjectBridge().SetPatternNameAsync(2, "Chorus");
            Assert.False(second.IsCompleted);
            Assert.Equal(resets, _transport.ScratchRequests);
            Assert.Equal("pong", await new FlInjectBridge().RawAsync("ping").WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { _transport.Release.TrySetResult(); }
        await second!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Chorus", _transport.Names[2]);
        if (pausePrefix != "scratch" && !nativeFault) Assert.Equal("Verse", _transport.Names[1]);
    }

    [Fact]
    public async Task WaitingForAbandonedNativeScratchIsBoundedAndDoesNotReleaseItsLease()
    {
        using var cancellation = new CancellationTokenSource();
        _transport.PauseWhen = command => command.StartsWith("call 11d3960 ", StringComparison.Ordinal);
        FlInjectBridge.Transport = (command, timeout, ct) => InProcBridge.RawAsync(command, timeout, ct, _transport.RunNative);
        Task first = new FlInjectBridge().SetPatternNameAsync(1, "Verse", cancellation.Token);
        await _transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
            var error = await Assert.ThrowsAsync<TimeoutException>(() =>
                new FlInjectBridge().SetPatternNameAsync(2, "Busy").WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("scratch buffer", error.Message);
            Assert.Equal(1, _transport.ScratchRequests);
        }
        finally { _transport.Release.TrySetResult(); }
        await new FlInjectBridge().SetPatternNameAsync(3, "Recovered").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Verse", _transport.Names[1]);
        Assert.Equal("Recovered", _transport.Names[3]);
        Assert.False(_transport.Names.ContainsKey(2));
    }

    [Fact]
    public async Task CancelledWaiterDoesNotAcquireOrReleaseAnotherOperationsLease()
    {
        _transport.PauseWhen = command => command.StartsWith("pokeabs ", StringComparison.Ordinal);
        Task first = new FlInjectBridge().SetPatternNameAsync(1, "Verse");
        await _transport.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        Task cancelled = new FlInjectBridge().SetPatternNameAsync(2, "Skipped", cancellation.Token);
        cancellation.Cancel();
        Task third = new FlInjectBridge().SetPatternNameAsync(3, "Bridge");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, _transport.ScratchRequests);
        }
        finally { _transport.Release.TrySetResult(); }
        await Task.WhenAll(first, third).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, _transport.ScratchRequests);
        Assert.Equal("Bridge", _transport.Names[3]);
        Assert.False(_transport.Names.ContainsKey(2));
    }

    [Fact]
    public async Task SaveAsKeepsPathAndOutputInsideSingleScratchAllocation()
    {
        const string path = @"C:\Sessions\Track.flp";
        await new FlInjectBridge().SaveProjectAsAsync(path).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(path, _transport.CurrentPath);
        Assert.Equal(path, Assert.Single(_transport.SavedPaths));
        Assert.Equal(2, _transport.ScratchRequests); // one SetProjectPath sequence, one Save sequence
        Assert.Equal(ScratchTransport.ScratchAddress + 1016, _transport.LastSaveOutput);
    }

    [Fact]
    public async Task SaveWithExplicitPathDoesNotClearPathWhilePreparingOutput()
    {
        const string path = @"C:\Sessions\Copy.flp";
        await new FlInjectBridge().SaveProjectAsync(path).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(path, Assert.Single(_transport.SavedPaths));
        Assert.Equal(1, _transport.ScratchRequests);
    }

    [Fact]
    public async Task ScratchInitializationFailureReleasesLease()
    {
        _transport.FailNextScratch = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FlInjectBridge().SetPatternNameAsync(1, "Failed"));
        await new FlInjectBridge().SetPatternNameAsync(2, "Success").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Success", _transport.Names[2]);
    }

    [Fact]
    public async Task OversizedStringCannotOverwriteScratchAndReleasesLease()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new FlInjectBridge().SetPatternNameAsync(1, new string('x', 502)));
        string maximum = new('y', 501);
        await new FlInjectBridge().SetPatternNameAsync(2, maximum).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(maximum, _transport.Names[2]);
        Assert.False(_transport.Names.ContainsKey(1));
    }

    private sealed class ScratchTransport
    {
        public const ulong ScratchAddress = 0x10000;
        private const ulong HeapAddress = 0x20000;
        private const string Ok = "{\"ok\":1,\"ret\":\"0x1\"}";
        private readonly byte[] _scratch = new byte[1024];
        private readonly Dictionary<ulong, byte[]> _heap = new();
        private int _paused;
        public int ScratchRequests { get; private set; }
        public bool FailNextScratch { get; set; }
        public bool FailPausedWork { get; set; }
        public Dictionary<int, string> Names { get; } = new();
        public List<string> SavedPaths { get; } = new();
        public string CurrentPath { get; private set; } = @"C:\Sessions\Existing.flp";
        public ulong LastSaveOutput { get; private set; }
        public Func<string, bool>? PauseWhen { get; set; }
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> SendAsync(string command, int timeout, CancellationToken ct)
        {
            string reply = Execute(command);
            if (PauseWhen?.Invoke(command) == true && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Paused.TrySetResult();
                // Deterministically interleave the simple fake. Timeout/cancellation tests instead
                // use RunNative through InProcBridge's real Task.Run/WaitAsync adapter below.
                await Release.Task.ConfigureAwait(false);
            }
            return reply;
        }

        public string RunNative(string command)
        {
            if (PauseWhen?.Invoke(command) == true && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Paused.TrySetResult();
                Release.Task.GetAwaiter().GetResult(); // blocking native work cannot observe caller cancellation
                if (FailPausedWork) throw new InvalidOperationException("Native work failed after its caller stopped waiting.");
            }
            return Execute(command);
        }

        private string Execute(string command)
        {
            string[] parts = command.Split(' ');
            return parts[0] switch
            {
                "scratch" => ResetScratch(),
                "ping" => "pong",
                "pokeabs" => Poke(Parse(parts[1]), Convert.FromHexString(parts[2])),
                "peekabs" => Convert.ToHexString(Read(Parse(parts[1]), int.Parse(parts[2], CultureInfo.InvariantCulture))),
                "resolve" when parts[1] == "sym:NoteRecorderArrayBase" => "80000",
                "peek" => ReadGlobal(parts[1]),
                "call" => Call(parts),
                _ => throw new InvalidOperationException("Unexpected fake command: " + command),
            };
        }

        private string ResetScratch()
        {
            ScratchRequests++;
            if (FailNextScratch)
            {
                FailNextScratch = false;
                return "err scratch unavailable";
            }
            Array.Clear(_scratch);
            return ScratchAddress.ToString("x", CultureInfo.InvariantCulture);
        }

        private string Poke(ulong address, byte[] bytes)
        {
            Assert.InRange(address, ScratchAddress, ScratchAddress + (ulong)_scratch.Length - (ulong)bytes.Length);
            bytes.CopyTo(_scratch, checked((int)(address - ScratchAddress)));
            return Ok;
        }

        private byte[] Read(ulong address, int count)
        {
            if (address >= 0x80000 && address + (ulong)count <= 0x80000 + 1000 * 0xC0) return new byte[count];
            if (address == 0x70000) return BitConverter.GetBytes(0x71000UL);
            if (address == 0x71010) return BitConverter.GetBytes(4);
            if (address >= ScratchAddress && address < ScratchAddress + (ulong)_scratch.Length)
                return _scratch.AsSpan(checked((int)(address - ScratchAddress)), count).ToArray();
            foreach (var allocation in _heap)
                if (address >= allocation.Key && address < allocation.Key + (ulong)allocation.Value.Length)
                    return allocation.Value.AsSpan(checked((int)(address - allocation.Key)), count).ToArray();
            throw new InvalidOperationException($"Unknown fake pointer {address:x}.");
        }

        private string Call(string[] parts)
        {
            switch (parts[1])
            {
                case "11d3960":
                case "11fb0d0":
                    Names[checked((int)Parse(parts[2]))] = ReadString(Parse(parts[3]));
                    break;
                case "11fb160":
                    ulong pointer = StoreString("Arrangement " + Parse(parts[3]), HeapAddress + Parse(parts[3]) * 0x1000);
                    Poke(Parse(parts[2]), BitConverter.GetBytes(pointer));
                    break;
                case "10d2c90":
                    CurrentPath = ReadString(Parse(parts[3]));
                    break;
                case "10d6190":
                    SavedPaths.Add(ReadString(Parse(parts[3])));
                    LastSaveOutput = Parse(parts[5]);
                    Poke(LastSaveOutput, BitConverter.GetBytes(1UL));
                    break;
                default: throw new InvalidOperationException("Unexpected fake call " + parts[1]);
            }
            return Ok;
        }

        private string ReadGlobal(string address)
        {
            ulong value = address switch
            {
                "1581200" => 0x40000,
                "1581298" => StoreString(CurrentPath, HeapAddress),
                "14a98d8" => 0x70000,
                _ => throw new InvalidOperationException("Unexpected fake global " + address),
            };
            return Convert.ToHexString(BitConverter.GetBytes(value));
        }

        private ulong StoreString(string value, ulong address)
        {
            byte[] data = new byte[4 + value.Length * 2];
            BitConverter.GetBytes(value.Length).CopyTo(data, 0);
            Encoding.Unicode.GetBytes(value).CopyTo(data, 4);
            _heap[address] = data;
            return address + 4;
        }

        private string ReadString(ulong chars)
        {
            int length = BitConverter.ToInt32(Read(chars - 4, 4));
            return Encoding.Unicode.GetString(Read(chars, length * 2));
        }

        private static ulong Parse(string hex) => ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
