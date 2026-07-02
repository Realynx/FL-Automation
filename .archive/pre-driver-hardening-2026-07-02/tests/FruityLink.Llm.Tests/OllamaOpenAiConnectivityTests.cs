using System.Net;
using System.Net.Http;
using FruityLink.Core.Configuration;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

public sealed class OllamaOpenAiConnectivityTests
{
    private static LlmSettings Settings => new(
        LlmBackendKind.Ollama, "http://localhost:11434/v1", "qwen2.5");

    [Fact]
    public async Task IsReachableAsync_True_OnSuccess()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[]}""");
        using var http = new HttpClient(handler);
        var probe = new OllamaOpenAiConnectivity(http);

        bool reachable = await probe.IsReachableAsync(Settings);

        reachable.ShouldBeTrue();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://localhost:11434/v1/models");
    }

    [Fact]
    public async Task IsReachableAsync_False_On503_NoException()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, "unavailable");
        using var http = new HttpClient(handler);
        var probe = new OllamaOpenAiConnectivity(http);

        bool reachable = await probe.IsReachableAsync(Settings);

        reachable.ShouldBeFalse();
    }

    [Fact]
    public async Task IsReachableAsync_False_WhenHandlerThrows_NoException()
    {
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("connection refused"));
        using var http = new HttpClient(handler);
        var probe = new OllamaOpenAiConnectivity(http);

        bool reachable = await probe.IsReachableAsync(Settings);

        reachable.ShouldBeFalse();
    }
}
