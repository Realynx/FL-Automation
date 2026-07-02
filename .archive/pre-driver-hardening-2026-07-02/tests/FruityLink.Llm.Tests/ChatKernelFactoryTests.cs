using System.IO;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

public sealed class ChatKernelFactoryTests
{
    private readonly ChatKernelFactory _factory = new(new LlmDiagnostics(Path.GetTempPath()));

    [Fact]
    public void CreateKernel_Ollama_ResolvesChatService_WithoutNetwork()
    {
        var settings = new LlmSettings(LlmBackendKind.Ollama, "http://localhost:11434/v1", "qwen2.5");

        Kernel kernel = _factory.CreateKernel(settings);

        kernel.ShouldNotBeNull();
        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void CreateKernel_OpenAI_ResolvesChatService_WithoutNetwork()
    {
        var settings = new LlmSettings(LlmBackendKind.OpenAI, "https://api.openai.com/v1", "gpt-4o");

        Kernel kernel = _factory.CreateKernel(settings, apiKey: "sk-test");

        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void CreateKernel_Anthropic_UsesOpenAiConnector_WithoutNetwork()
    {
        // Anthropic is reached as an OpenAI-compatible proxy in this layer.
        var settings = new LlmSettings(LlmBackendKind.Anthropic, "https://proxy.local/v1", "claude-3-5-sonnet");

        Kernel kernel = _factory.CreateKernel(settings, apiKey: "key");

        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void CreateKernel_Azure_ResolvesChatService_WithoutNetwork()
    {
        var settings = new LlmSettings(
            LlmBackendKind.AzureOpenAI,
            "https://example.openai.azure.com/",
            "gpt-4o",
            Deployment: "my-deployment");

        Kernel kernel = _factory.CreateKernel(settings, apiKey: "azure-key");

        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void CreateChatService_Ollama_ReturnsService_WithoutNetwork()
    {
        var settings = new LlmSettings(LlmBackendKind.Ollama, "http://localhost:11434/v1", "qwen2.5");

        IChatCompletionService service = _factory.CreateChatService(settings);

        service.ShouldNotBeNull();
    }
}
