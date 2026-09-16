using FruityLink.Plugins.Abstractions;
using FruityLink.Plugins.PythonIde.Ui;
using Xunit;

namespace FruityLink.Plugins.PythonIde.Ui.Tests;

public sealed class EmbeddingDiagnosticTests
{
    private static readonly FlWindowOptions Options = new("FL Python IDE", 1120, 780, 820, 560);

    [Fact]
    public async Task NativeCreationRefusalRecordsItsBridgeReasonWithoutSynchronousProbe()
    {
        const string reply = """{"ok":0,"exists":0,"reason":"native-content-bounds-unavailable"}""";
        var host = new FakeHost { LastEmbedReply = reply };
        var messages = new List<string>();
        Assert.False(await PythonIdeWindowHost.EmbedWindowAsync(host, (IntPtr)123, Options, messages.Add, default));
        Assert.Contains(reply, Assert.Single(messages));
        Assert.Equal((IntPtr)123, host.Child);
        Assert.Equal(Options, host.Options);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    public async Task MissingBridgeReasonIsReportedExplicitly(string reply)
    {
        var messages = new List<string>();
        Assert.False(await PythonIdeWindowHost.EmbedWindowAsync(new FakeHost { LastEmbedReply = reply },
            (IntPtr)123, Options, messages.Add, default));
        Assert.Contains("(no bridge diagnostic)", Assert.Single(messages));
    }

    [Fact]
    public async Task UntrustedDiagnosticCannotAddLogLinesOrUnboundedOutput()
    {
        var messages = new List<string>();
        var host = new FakeHost { LastEmbedReply = "err:geometry\r\n\t\0\u2028\u2029" + new string('x', 5000) };
        Assert.False(await PythonIdeWindowHost.EmbedWindowAsync(host, (IntPtr)123, Options, messages.Add, default));
        string message = Assert.Single(messages);
        Assert.Contains("err:geometry", message);
        Assert.EndsWith(" [truncated]", message);
        Assert.True(message.Length < 2200);
        Assert.DoesNotContain(message, character => char.IsControl(character) || character is '\u2028' or '\u2029');
    }

    [Fact]
    public async Task SuccessfulEmbeddingDoesNotClaimAnExternalFallback()
    {
        var messages = new List<string>();
        Assert.True(await PythonIdeWindowHost.EmbedWindowAsync(new FakeHost { Embedded = true },
            (IntPtr)123, Options, messages.Add, default));
        Assert.Empty(messages);
    }

    [Fact]
    public async Task UnconfirmedCleanupFailurePropagatesWithoutClaimingSafeFallback()
    {
        var messages = new List<string>();
        var failure = new InvalidOperationException("Native rollback is incomplete.");
        var host = new FakeHost { Failure = failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PythonIdeWindowHost.EmbedWindowAsync(host, (IntPtr)123, Options, messages.Add, default)));
        Assert.Empty(messages);
    }

    private sealed class FakeHost : IAsyncFlWindowHost
    {
        public string LastEmbedReply { get; init; } = "";
        public int LastInsetX => 0;
        public int LastInsetY => 0;
        public bool Embedded { get; init; }
        public Exception? Failure { get; init; }
        public IntPtr Child { get; private set; }
        public FlWindowOptions? Options { get; private set; }
        public Task<bool> TryEmbedAsync(IntPtr childHwnd, FlWindowOptions options, bool show = true,
            CancellationToken cancellationToken = default)
        {
            Child = childHwnd;
            Options = options;
            return Failure is null ? Task.FromResult(Embedded) : Task.FromException<bool>(Failure);
        }
        public bool IsBridgeAvailable() => throw new InvalidOperationException("Synchronous probe must not be called.");
        public bool TryEmbed(IntPtr childHwnd, bool show) => throw new NotSupportedException();
        public Task<bool> IsBridgeAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public bool IsHostVisible() => Embedded;
        public void SetVisible(bool visible) => throw new NotSupportedException();
        public void Close() => throw new NotSupportedException();
        public void SetStatusHint(string text) => throw new NotSupportedException();
        public Task<bool> SetVisibleAsync(bool visible, bool activate = false, CancellationToken cancellationToken = default)
            => Task.FromResult(Embedded);
        public Task<bool> CloseAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
