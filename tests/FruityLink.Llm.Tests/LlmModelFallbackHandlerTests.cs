using System.Net;
using System.Net.Http;
using System.Text.Json;
using FruityLink.Llm.Diagnostics;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// Behavior contract for <see cref="LlmModelFallbackHandler"/>: the gateway's
/// "Model 'X' is not allowed" 400 (a stale saved model after a plan change) is resent ONCE with
/// <c>model: "default"</c> and reported through the heal hook; every other 400, non-chat endpoints,
/// and an already-default request pass through untouched (body still readable); the fallback never
/// loops when the resend is rejected too.
/// </summary>
public sealed class LlmModelFallbackHandlerTests
{
    private static readonly Uri ChatUri = new("https://ai.fl-automate.com/v1/chat/completions");

    private const string NotAllowedBody =
        """{"message":"Model 'deepseek' is not allowed. Allowed models: glm, gemma, qwen, minimax","error":"Bad Request","statusCode":400}""";

    private static HttpRequestMessage ChatRequest(string model = "deepseek", string uri = "") =>
        new(HttpMethod.Post, uri.Length > 0 ? new Uri(uri) : ChatUri)
        {
            Content = new StringContent(
                $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}],"stream":true}""",
                System.Text.Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task Send_ModelNotAllowed400_ResendsOnceAsDefaultAndReturnsTheRetry()
    {
        var inner = FakeHttpMessageHandler.From(request =>
        {
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("\"model\":\"default\"", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"ok":true}""")
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.BadRequest, NotAllowedBody);
        });
        var diagnostics = new RecordingLlmDiagnostics();
        using var invoker = new HttpMessageInvoker(new LlmModelFallbackHandler(inner, diagnostics));

        HttpResponseMessage response = await invoker.SendAsync(ChatRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.RequestCount.ShouldBe(2);

        // The resend is the ORIGINAL request with only the model swapped (messages intact).
        using JsonDocument resent = JsonDocument.Parse(inner.RequestBodies[1]!);
        resent.RootElement.GetProperty("model").GetString().ShouldBe("default");
        resent.RootElement.GetProperty("messages").GetArrayLength().ShouldBe(1);
        resent.RootElement.GetProperty("stream").GetBoolean().ShouldBeTrue();
        inner.Requests[1].Content!.Headers.ContentLength.ShouldBe(
            System.Text.Encoding.UTF8.GetByteCount(inner.RequestBodies[1]!));

        // The fallback is observable in the diagnostics log.
        diagnostics.Records.ShouldContain(r => r.Method == "FALLBACK");
    }

    [Fact]
    public async Task Send_ModelNotAllowed400_ReportsTheStaleModelOncePerSession()
    {
        var inner = FakeHttpMessageHandler.From(request =>
        {
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return body.Contains("\"model\":\"default\"", StringComparison.Ordinal)
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"ok":true}""")
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.BadRequest, NotAllowedBody);
        });
        var fallbacks = new List<LlmModelFallback>();
        using var invoker = new HttpMessageInvoker(
            new LlmModelFallbackHandler(inner, new RecordingLlmDiagnostics(), fallbacks.Add));

        // Two turns with the same stale model: both are rescued, but the heal hook fires once.
        (await invoker.SendAsync(ChatRequest(), CancellationToken.None)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await invoker.SendAsync(ChatRequest(), CancellationToken.None)).StatusCode.ShouldBe(HttpStatusCode.OK);

        LlmModelFallback fallback = fallbacks.ShouldHaveSingleItem();
        fallback.StaleModel.ShouldBe("deepseek");
        fallback.AllowedModels.ShouldBe(new[] { "glm", "gemma", "qwen", "minimax" });
    }

    [Fact]
    public async Task Send_Other400_PassesThroughWithAReadableBody()
    {
        const string validation = """{"message":["messages must be an array"],"error":"Bad Request","statusCode":400}""";
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, validation);
        using var invoker = new HttpMessageInvoker(
            new LlmModelFallbackHandler(inner, new RecordingLlmDiagnostics()));

        HttpResponseMessage response = await invoker.SendAsync(ChatRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        inner.RequestCount.ShouldBe(1);
        // The handler read the body to inspect it — downstream must still be able to read it.
        (await response.Content.ReadAsStringAsync()).ShouldBe(validation);
    }

    [Fact]
    public async Task Send_DefaultModelRejected_DoesNotLoop()
    {
        // A rejected "default" means the plan menu itself is broken — resending can't help.
        const string body =
            """{"message":"Model 'default' is not allowed. Allowed models: glm","error":"Bad Request","statusCode":400}""";
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, body);
        using var invoker = new HttpMessageInvoker(
            new LlmModelFallbackHandler(inner, new RecordingLlmDiagnostics()));

        HttpResponseMessage response =
            await invoker.SendAsync(ChatRequest(model: "default"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        inner.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Send_FallbackAlsoRejected_ReturnsThatRejection()
    {
        // Stale model AND a broken menu: the resend 400s too — return it, no third attempt.
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, NotAllowedBody);
        using var invoker = new HttpMessageInvoker(
            new LlmModelFallbackHandler(inner, new RecordingLlmDiagnostics()));

        HttpResponseMessage response = await invoker.SendAsync(ChatRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        inner.RequestCount.ShouldBe(2); // original + one fallback, never more
    }

    [Fact]
    public async Task Send_NonChatEndpoint400_IsUntouched()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, NotAllowedBody);
        using var invoker = new HttpMessageInvoker(
            new LlmModelFallbackHandler(inner, new RecordingLlmDiagnostics()));

        HttpResponseMessage response = await invoker.SendAsync(
            ChatRequest(uri: "https://ai.fl-automate.com/v1/embeddings"), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        inner.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task Send_Success_PassesThroughUntouched()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"ok":true}""");
        var diagnostics = new RecordingLlmDiagnostics();
        using var invoker = new HttpMessageInvoker(new LlmModelFallbackHandler(inner, diagnostics));

        HttpResponseMessage response = await invoker.SendAsync(ChatRequest(), CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.RequestCount.ShouldBe(1);
        diagnostics.Records.ShouldBeEmpty();
    }
}
