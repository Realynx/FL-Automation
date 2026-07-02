using System.Net;
using System.Net.Http;
using System.Text;
using FruityLink.Core.Abstractions;
using FruityLink.Llm.Diagnostics;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// Behavior contract for <see cref="LlmLoggingHandler"/>: failures capture request/response bodies
/// while keeping the response readable downstream (rebuffered), successful chat completions record
/// usage-block token/byte telemetry, and telemetry never breaks the call path (sinks without the
/// optional capability degrade gracefully; exceptions are recorded and rethrown).
/// </summary>
public sealed class LlmLoggingHandlerTests
{
    private const string ChatUri = "http://localhost:11434/v1/chat/completions";

    [Fact]
    public async Task Failure_CapturesBodies_AndResponseStaysReadableDownstream()
    {
        const string errorBody = """{"error":"boom"}""";
        const string requestBody = """{"model":"m","messages":[]}""";
        var diagnostics = new RecordingLlmDiagnostics();
        using var client = new HttpClient(new LlmLoggingHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, errorBody), diagnostics));

        HttpResponseMessage response = await client.PostAsync(ChatUri, JsonContent(requestBody));

        // The logger consumed the body for capture — downstream (SK) must still get it intact,
        // with the original media type (error pages are not always JSON, so it is preserved).
        (await response.Content.ReadAsStringAsync()).ShouldBe(errorBody);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        LlmCallRecord record = diagnostics.Records.ShouldHaveSingleItem();
        record.Ok.ShouldBeFalse();
        record.StatusCode.ShouldBe(500);
        record.Attempt.ShouldBe(1); // no AttemptKey option -> defaults to attempt 1
        record.Error!.ShouldContain("500");
        record.RequestPreview!.ShouldContain("\"model\"");
        record.ResponsePreview!.ShouldContain("boom");
    }

    [Fact]
    public async Task Failure_LongBody_TruncatedInPreview_ButFullDownstream()
    {
        string errorBody = new string('x', 7000); // past the 6000-char preview cap
        var diagnostics = new RecordingLlmDiagnostics();
        using var client = new HttpClient(new LlmLoggingHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, errorBody), diagnostics));

        HttpResponseMessage response = await client.PostAsync(ChatUri, JsonContent("{}"));

        (await response.Content.ReadAsStringAsync()).Length.ShouldBe(7000); // capture never trims the real body
        LlmCallRecord record = diagnostics.Records.ShouldHaveSingleItem();
        record.ResponsePreview!.ShouldContain("(7000 chars total)");
        record.ResponsePreview!.Length.ShouldBeLessThan(6100);
    }

    [Fact]
    public async Task Success_ChatCompletion_RecordsUsageTokens_AndByteMeasurements()
    {
        // toolsSpan is the exact "tools" key-through-] span so the expected ToolsBytes is a
        // straight byte count of a known constant, not a re-implementation of the measurer.
        const string toolsSpan = "\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"set_tempo\",\"parameters\":{\"type\":\"object\"}}}]";
        const string requestJson = "{\"model\":\"m\"," + toolsSpan + ",\"messages\":[]}";
        const string responseJson = """{"choices":[{"message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":123,"completion_tokens":45}}""";
        var diagnostics = new RecordingLlmDiagnostics();
        using var client = new HttpClient(new LlmLoggingHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, responseJson), diagnostics));

        HttpResponseMessage response = await client.PostAsync(ChatUri, JsonContent(requestJson));

        LlmUsageSample sample = diagnostics.UsageSamples.ShouldHaveSingleItem();
        sample.PromptTokens.ShouldBe(123);
        sample.CompletionTokens.ShouldBe(45);
        sample.RequestBytes.ShouldBe((long)Encoding.UTF8.GetByteCount(requestJson));
        sample.ToolsBytes.ShouldBe((long)Encoding.UTF8.GetByteCount(toolsSpan));

        // Usage parsing buffered the body — downstream must still read it in full.
        (await response.Content.ReadAsStringAsync()).ShouldBe(responseJson);

        LlmCallRecord record = diagnostics.Records.ShouldHaveSingleItem();
        record.Ok.ShouldBeTrue();
        record.RequestPreview.ShouldBeNull(); // bodies are only captured on failure
        record.ResponsePreview.ShouldBeNull();
    }

    [Fact]
    public async Task Success_WithoutToolsInRequest_RecordsZeroToolsBytes()
    {
        const string responseJson = """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2}}""";
        var diagnostics = new RecordingLlmDiagnostics();
        using var client = new HttpClient(new LlmLoggingHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, responseJson), diagnostics));

        await client.PostAsync(ChatUri, JsonContent("""{"model":"m","messages":[]}"""));

        diagnostics.UsageSamples.ShouldHaveSingleItem().ToolsBytes.ShouldBe(0);
    }

    [Fact]
    public async Task Success_NonChatCompletionUri_SkipsUsageTelemetry()
    {
        const string responseJson = """{"data":[],"usage":{"prompt_tokens":10,"completion_tokens":0}}""";
        var diagnostics = new RecordingLlmDiagnostics();
        using var client = new HttpClient(new LlmLoggingHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, responseJson), diagnostics));

        await client.PostAsync("http://localhost:11434/v1/embeddings", JsonContent("""{"input":"x"}"""));

        diagnostics.UsageSamples.ShouldBeEmpty(); // usage is a chat-completions concept
        diagnostics.Records.ShouldHaveSingleItem().Ok.ShouldBeTrue(); // the call itself is still logged
    }

    [Fact]
    public async Task Success_SinkWithoutUsageCapability_StillRecordsCall_WithoutThrowing()
    {
        const string responseJson = """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2}}""";
        var diagnostics = new PlainRecordingDiagnostics(); // no ILlmUsageTelemetry — soft cast fails
        using var client = new HttpClient(new LlmLoggingHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, responseJson), diagnostics));

        HttpResponseMessage response = await client.PostAsync(ChatUri, JsonContent("""{"model":"m"}"""));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        diagnostics.Records.ShouldHaveSingleItem().Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Exception_RecordsFailureWithRequestBody_AndRethrows()
    {
        const string requestBody = """{"model":"m","messages":[]}""";
        var diagnostics = new RecordingLlmDiagnostics();
        using var client = new HttpClient(new LlmLoggingHandler(
            FakeHttpMessageHandler.Throws(new HttpRequestException("connection refused")), diagnostics));

        await Should.ThrowAsync<HttpRequestException>(() => client.PostAsync(ChatUri, JsonContent(requestBody)));

        LlmCallRecord record = diagnostics.Records.ShouldHaveSingleItem();
        record.Ok.ShouldBeFalse();
        record.StatusCode.ShouldBeNull(); // threw before any response
        record.Error!.ShouldContain(nameof(HttpRequestException));
        record.Error!.ShouldContain("connection refused");
        record.RequestPreview!.ShouldContain("\"model\"");
    }

    [Fact]
    public async Task Send_RecordsAttemptNumber_FromRequestOptions()
    {
        var diagnostics = new RecordingLlmDiagnostics();
        using var invoker = new HttpMessageInvoker(new LlmLoggingHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"), diagnostics));
        var request = new HttpRequestMessage(HttpMethod.Post, ChatUri)
        {
            Content = JsonContent("""{"model":"m"}"""),
        };
        request.Options.Set(LlmLoggingHandler.AttemptKey, 2); // as stamped by LlmRetryHandler clones

        await invoker.SendAsync(request, CancellationToken.None);

        diagnostics.Records.ShouldHaveSingleItem().Attempt.ShouldBe(2);
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private static StringContent JsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Bare <see cref="ILlmDiagnostics"/> WITHOUT the optional telemetry capabilities —
    /// proves the handler degrades gracefully when a custom sink lacks them.</summary>
    private sealed class PlainRecordingDiagnostics : ILlmDiagnostics
    {
        public List<LlmCallRecord> Records { get; } = [];

        public string LogDirectory => string.Empty;
        public IReadOnlyList<LlmCallRecord> Recent => Records;
        public event EventHandler? Changed { add { } remove { } }

        public void Record(LlmCallRecord record) => Records.Add(record);

        public void Clear() => Records.Clear();
    }
}
