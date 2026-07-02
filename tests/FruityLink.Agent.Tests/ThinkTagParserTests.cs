using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// <see cref="ThinkTagParser"/> must split reasoning from answer text for every marker shape weak
/// backends and proxies actually emit — including the malformed halves (orphan open/close) that
/// previously leaked raw markers into the user-visible answer and stored history.
/// </summary>
public sealed class ThinkTagParserTests
{
    [Theory]
    // No marker: content passes through untouched as the answer.
    [InlineData("just an answer", "", "just an answer")]
    // Normal pair.
    [InlineData("<think>plan</think>answer", "plan", "answer")]
    // Marker mid-text: answer segments around the block are spliced together.
    [InlineData("A <think>plan</think>B", "plan", "A B")]
    // Unclosed open (the fixed leak): everything after the orphan open is reasoning — the raw
    // marker and half-finished reasoning must NOT surface as answer text.
    [InlineData("answer so far<think>half-finished reasoning", "half-finished reasoning", "answer so far")]
    // Orphan close: the open marker was eaten upstream, so text BEFORE the close is reasoning.
    [InlineData("upstream reasoning</think>the answer", "upstream reasoning", "the answer")]
    // Markers match case-insensitively.
    [InlineData("<THINK>plan</ThInK>answer", "plan", "answer")]
    // Multiple blocks: reasoning segments join with newlines, answer segments concatenate in order.
    [InlineData("a<think>t1</think>b<think>t2</think>c", "t1\nt2", "abc")]
    // Both sides are whitespace-trimmed.
    [InlineData("<think>  plan  </think>  answer  ", "plan", "answer")]
    // Marker-only content: all reasoning, empty answer.
    [InlineData("<think>plan</think>", "plan", "")]
    public void Split_SeparatesThoughtFromText(string content, string expectedThought, string expectedText)
    {
        (string thought, string text) = ThinkTagParser.Split(content);

        thought.ShouldBe(expectedThought);
        text.ShouldBe(expectedText);
    }

    [Fact]
    public void Split_EmptyContent_ReturnsEmptyPair()
    {
        (string thought, string text) = ThinkTagParser.Split(string.Empty);

        thought.ShouldBe(string.Empty);
        text.ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("<think>", true)]
    [InlineData("</THINK>", true)]
    [InlineData("prefix <Think> suffix", true)]
    [InlineData("no marker at all", false)]
    [InlineData("think without brackets", false)]
    public void ContainsMarker_DetectsEitherMarkerCaseInsensitively(string content, bool expected) =>
        ThinkTagParser.ContainsMarker(content).ShouldBe(expected);
}
