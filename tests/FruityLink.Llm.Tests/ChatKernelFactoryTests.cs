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
    private readonly ChatKernelFactory _factory = new(
        new LlmDiagnostics(Path.GetTempPath()), new FakeAccountAuth());

    [Fact]
    public void CreateKernel_DefaultAccount_ResolvesChatService_WithoutNetwork()
    {
        Kernel kernel = _factory.CreateKernel(new AccountSettings());

        kernel.ShouldNotBeNull();
        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void CreateKernel_CustomGatewayAndModel_ResolvesChatService_WithoutNetwork()
    {
        var settings = new AccountSettings(
            GatewayBaseUrl: "http://localhost:8787",
            Model: "big-brain");

        Kernel kernel = _factory.CreateKernel(settings);

        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void CreateKernel_BlankModel_FallsBackToDefaultSentinel()
    {
        // "default" is the gateway's plan-default sentinel; a blank model must map to it rather
        // than tripping the connector's non-empty-model validation.
        var settings = new AccountSettings(Model: "  ");

        settings.ModelOrDefault.ShouldBe("default");
        Kernel kernel = _factory.CreateKernel(settings);
        kernel.GetRequiredService<IChatCompletionService>().ShouldNotBeNull();
    }

    [Fact]
    public void GatewayOpenAiBase_ComposesV1_TrimmingTrailingSlash()
    {
        new AccountSettings(GatewayBaseUrl: "https://ai.fl-automate.com/").GatewayOpenAiBase
            .ShouldBe("https://ai.fl-automate.com/v1");
        new AccountSettings().GatewayOpenAiBase.ShouldBe("https://ai.fl-automate.com/v1");
    }

    [Fact]
    public void CreateChatService_ReturnsService_WithoutNetwork()
    {
        IChatCompletionService service = _factory.CreateChatService(new AccountSettings());

        service.ShouldNotBeNull();
    }
}
