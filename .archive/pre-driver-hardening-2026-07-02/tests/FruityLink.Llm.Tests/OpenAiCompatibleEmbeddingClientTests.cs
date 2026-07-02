using System.Net;
using System.Net.Http;
using FruityLink.Core.Configuration;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

public sealed class OpenAiCompatibleEmbeddingClientTests
{
    private static LlmSettings OllamaEmbedSettings => new(
        Backend: LlmBackendKind.Ollama,
        Endpoint: "http://localhost:11434/v1/",
        Model: "nomic-embed-text");

    [Fact]
    public async Task EmbedAsync_Single_ParsesVector()
    {
        const string json = """
        {"data":[{"embedding":[0.1,0.2,0.3]}]}
        """;
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(http, OllamaEmbedSettings);

        float[] vector = await client.EmbedAsync("hello");

        vector.ShouldBe(new[] { 0.1f, 0.2f, 0.3f });
    }

    [Fact]
    public async Task EmbedAsync_Batch_ParsesAllVectorsAndPreservesOrder()
    {
        // Distinct first components let us assert the order is preserved.
        const string json = """
        {"data":[
          {"embedding":[1.0,1.1]},
          {"embedding":[2.0,2.1]},
          {"embedding":[3.0,3.1]}
        ]}
        """;
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, json);
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(http, OllamaEmbedSettings);

        IReadOnlyList<float[]> vectors = await client.EmbedAsync(new[] { "a", "b", "c" });

        vectors.Count.ShouldBe(3);
        vectors[0].ShouldBe(new[] { 1.0f, 1.1f });
        vectors[1].ShouldBe(new[] { 2.0f, 2.1f });
        vectors[2].ShouldBe(new[] { 3.0f, 3.1f });
    }

    [Fact]
    public async Task EmbedAsync_ComposesUrlTrimmingTrailingSlash()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"embedding":[0.0]}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(http, OllamaEmbedSettings);

        await client.EmbedAsync("x");

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://localhost:11434/v1/embeddings");
    }

    [Fact]
    public async Task EmbedAsync_SendsAuthorizationHeader_WhenKeyProvided()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"embedding":[0.0]}]}""");
        using var http = new HttpClient(handler);
        var openAi = new LlmSettings(LlmBackendKind.OpenAI, "https://api.openai.com/v1", "text-embedding-3-small");
        var client = new OpenAiCompatibleEmbeddingClient(http, openAi, apiKey: "sk-secret");

        await client.EmbedAsync("x");

        var auth = handler.LastRequest!.Headers.Authorization;
        auth.ShouldNotBeNull();
        auth!.Scheme.ShouldBe("Bearer");
        auth.Parameter.ShouldBe("sk-secret");
    }

    [Fact]
    public async Task EmbedAsync_DoesNotSendAuthorization_WhenNoKey()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"data":[{"embedding":[0.0]}]}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(http, OllamaEmbedSettings);

        await client.EmbedAsync("x");

        handler.LastRequest!.Headers.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task EmbedAsync_Throws_OnServerError_WithStatusAndBody()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(http, OllamaEmbedSettings);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.EmbedAsync("x"));

        ex.Message.ShouldContain("500");
        ex.Message.ShouldContain("boom");
    }

    [Fact]
    public async Task EmbedAsync_EmptyBatch_ReturnsEmptyWithoutCallingServer()
    {
        var handler = FakeHttpMessageHandler.Throws(new InvalidOperationException("should not be called"));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleEmbeddingClient(http, OllamaEmbedSettings);

        IReadOnlyList<float[]> vectors = await client.EmbedAsync(Array.Empty<string>());

        vectors.ShouldBeEmpty();
        handler.LastRequest.ShouldBeNull();
    }
}
