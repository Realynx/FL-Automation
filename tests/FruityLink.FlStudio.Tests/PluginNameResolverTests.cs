using FruityLink.FlStudio;
using Xunit;

namespace FruityLink.FlStudio.Tests;

public sealed class PluginNameResolverTests
{
    private static readonly string[] Effects =
    [
        "Pro-R 2", "Pro-L 2", "Pro-Q 4", "Pro-C 2", "Fruity Reeverb 2", "Fruity Delay 3", "Fruity Limiter", "Serum 2 FX", "Gross Beat",
    ];

    [Theory]
    [InlineData("Pro-R 2", "Pro-R 2", "exact")]
    [InlineData("pro-r 2", "Pro-R 2", "exact")]
    [InlineData("  Pro-R 2 ", "Pro-R 2", "exact")]
    [InlineData("Pro R2", "Pro-R 2", "normalized")]
    [InlineData("pror2", "Pro-R 2", "normalized")]
    [InlineData("FabFilter Pro-R 2", "Pro-R 2", "contains")]
    [InlineData("Pro-R", "Pro-R 2", "contains")]
    [InlineData("Reeverb", "Fruity Reeverb 2", "contains")]
    [InlineData("Image-Line Gross Beat (VST3)", "Gross Beat", "contains")]
    public void ResolvesExactNormalizedAndContainedNames(string requested, string expected, string rule)
    {
        var match = PluginNameResolver.Match(requested, Effects);
        Assert.Equal(expected, match.Name);
        Assert.Equal(rule, match.Rule);
    }

    [Fact]
    public void AmbiguousContainmentRefusesAndListsEveryCandidate()
    {
        var match = PluginNameResolver.Match("Pro", Effects);
        Assert.Null(match.Name);
        Assert.Equal("ambiguous", match.Rule);
        Assert.Equal(new[] { "Pro-C 2", "Pro-L 2", "Pro-Q 4", "Pro-R 2" }, match.Candidates);
        string message = PluginNameResolver.DescribeFailure("Effect", "Pro", match);
        Assert.Contains("matches several installed plugins", message);
        Assert.Contains("'Pro-C 2', 'Pro-L 2', 'Pro-Q 4', 'Pro-R 2'", message);
    }

    [Fact]
    public void ExactNameWinsOverContainmentEvenWhenLongerNamesExist()
    {
        var match = PluginNameResolver.Match("Serum 2 FX", new[] { "Serum 2 FX", "Serum 2 FX (Legacy)" });
        Assert.Equal("Serum 2 FX", match.Name);
        Assert.Equal("exact", match.Rule);
    }

    [Fact]
    public void MissListsTheClosestInstalledNames()
    {
        var match = PluginNameResolver.Match("Valhalla Room", Effects);
        Assert.Null(match.Name);
        Assert.Equal("none", match.Rule);
        Assert.Equal(5, match.Candidates.Count);
        string message = PluginNameResolver.DescribeFailure("Effect", "Valhalla Room", match);
        Assert.StartsWith("Effect plugin 'Valhalla Room' not found. Closest installed names: '", message);
        Assert.Contains("list_available_plugins", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void BlankOrPunctuationOnlyNamesNeverMatch(string requested)
    {
        var match = PluginNameResolver.Match(requested, Effects);
        Assert.Null(match.Name);
        Assert.NotEqual("ambiguous", match.Rule);
    }

    [Fact]
    public void EmptyDatabaseReportsNothingInstalled()
    {
        var match = PluginNameResolver.Match("Pro-R 2", Array.Empty<string>());
        Assert.Null(match.Name);
        Assert.Contains("no plugins are installed", PluginNameResolver.DescribeFailure("Effect", "Pro-R 2", match));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "", 3)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("pror2", "pror2", 0)]
    public void EditDistanceIsLevenshtein(string a, string b, int expected)
        => Assert.Equal(expected, PluginNameResolver.EditDistance(a, b));
}
