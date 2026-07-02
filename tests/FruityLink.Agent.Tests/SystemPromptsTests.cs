using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// <see cref="SystemPrompts"/> composition rules: the caveman output-compression block (a paid
/// product feature) must be appended to BOTH the main and sub-agent prompts exactly when
/// <see cref="AgentPromptOptions.CavemanModeEnabled"/> says so, and the OK/ERR result-convention
/// line — the one contract every tool result relies on — must be stated exactly once (twice would
/// waste tokens; zero would leave weak models guessing what "ERR:" means).
/// </summary>
public sealed class SystemPromptsTests
{
    /// <summary>The single line teaching the model the shared tool-result envelope.</summary>
    private const string ResultConventionLine = "Tool results start OK (success) or ERR";

    /// <summary>Stable sentinel of the caveman block (its banner line).</summary>
    private const string CavemanSentinel = "OUTPUT STYLE: CAVEMAN SPEAK";

    [Fact]
    public void BuildDefault_CavemanOn_AppendsTheCavemanBlockAfterThePersona()
    {
        string prompt = SystemPrompts.BuildDefault(new AgentPromptOptions(CavemanModeEnabled: true));

        prompt.ShouldStartWith(SystemPrompts.Default);
        prompt.ShouldContain(CavemanSentinel);
    }

    [Fact]
    public void BuildDefault_CavemanOff_IsThePlainPersona()
    {
        string prompt = SystemPrompts.BuildDefault(new AgentPromptOptions(CavemanModeEnabled: false));

        prompt.ShouldBe(SystemPrompts.Default);
        prompt.ShouldNotContain(CavemanSentinel);
    }

    [Fact]
    public void CavemanModeDefaultsOn()
    {
        // Default ON is the product's token-margin posture; a silent flip would change costs.
        new AgentPromptOptions().CavemanModeEnabled.ShouldBeTrue();
    }

    [Fact]
    public void BuildSubAgent_CavemanOn_GetsTheSameCompressionAsTheMainAgent()
    {
        string prompt = SystemPrompts.BuildSubAgent(new AgentPromptOptions(CavemanModeEnabled: true));

        // Fanned-out sub-agents pay for their reasoning + summaries too.
        prompt.ShouldStartWith(SystemPrompts.SubAgent);
        prompt.ShouldContain(CavemanSentinel);
    }

    [Fact]
    public void BuildSubAgent_CavemanOff_IsThePlainSubAgentPrompt()
    {
        string prompt = SystemPrompts.BuildSubAgent(new AgentPromptOptions(CavemanModeEnabled: false));

        prompt.ShouldBe(SystemPrompts.SubAgent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildDefault_StatesTheResultConventionExactlyOnce(bool cavemanEnabled) =>
        CountOccurrences(SystemPrompts.BuildDefault(new AgentPromptOptions(cavemanEnabled)), ResultConventionLine)
            .ShouldBe(1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildSubAgent_StatesTheResultConventionExactlyOnce(bool cavemanEnabled) =>
        CountOccurrences(SystemPrompts.BuildSubAgent(new AgentPromptOptions(cavemanEnabled)), ResultConventionLine)
            .ShouldBe(1);

    [Fact]
    public void CavemanBlock_NeverTouchesToolCallPrecision()
    {
        // The carve-outs are the guardrail keeping compression away from the function-call format.
        SystemPrompts.CavemanPrompt.ShouldContain("CARVE-OUTS");
        SystemPrompts.CavemanPrompt.ShouldContain("stay EXACT + valid");
    }

    private static int CountOccurrences(string text, string fragment)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(fragment, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }
        return count;
    }
}
