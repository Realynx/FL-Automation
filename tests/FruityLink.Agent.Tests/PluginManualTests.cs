using FruityLink.Agent.Manuals;
using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// The plugin "man db": proves the embedded manuals load at runtime, that name lookup is forgiving
/// (case/space/punctuation/alias/typo), and that KnowledgePlugin's two tools surface them. This is the
/// foundation many plugins will be added to, so the tests pin the format contract, not just the seeds.
/// </summary>
public sealed class PluginManualTests
{
    private static KnowledgePlugin Plugin() => new(new EmptyRetriever());

    [Fact]
    public void ManualStore_Default_LoadsEmbeddedManualsAtRuntime()
    {
        // If the <EmbeddedResource> glob or deployment ever breaks, All goes empty — catch it here.
        ManualStore.Default.All.ShouldNotBeEmpty();
        ManualStore.Default.Names().ShouldContain(n => n.Contains("3xOSC"));
        ManualStore.Default.Names().ShouldContain(n => n.Contains("Reeverb", StringComparison.OrdinalIgnoreCase)
                                                    || n.Contains("Reverb", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("3xosc")]
    [InlineData("3x osc")]
    [InlineData("3xOSC")]
    [InlineData("3 osc")]
    [InlineData("three osc")]
    [InlineData("3xoscc")]     // typo → fuzzy
    public void Resolve_3xOsc_AcrossSpellings(string query)
    {
        PluginManual? m = ManualStore.Default.Resolve(query);
        m.ShouldNotBeNull();
        m!.Name.ShouldBe("3xOSC");
        m.Type.ShouldBe(ManualType.Generator);
    }

    [Theory]
    [InlineData("reverb")]
    [InlineData("Fruity Reverb 2")]
    [InlineData("reeverb 2")]
    public void Resolve_Reverb_ResolvesFruityReeverb2AsEffect(string query)
    {
        PluginManual? m = ManualStore.Default.Resolve(query);
        m.ShouldNotBeNull();
        m!.Type.ShouldBe(ManualType.Effect);
        m.Name.ShouldContain("everb"); // "Reeverb"/"Reverb" both contain it
    }

    [Fact]
    public void Resolve_Unknown_ReturnsNull()
    {
        ManualStore.Default.Resolve("definitely-not-a-plugin-xyzzy").ShouldBeNull();
    }

    [Fact]
    public void GetPluginManual_3xosc_ResolvesAndReturnsNonEmptyBody()
    {
        string result = Plugin().GetPluginManual("3xosc");

        result.ShouldStartWith("OK:");
        result.Length.ShouldBeGreaterThan(50);
        result.ShouldContain("oscillator", Case.Insensitive);
        result.ShouldContain("saw", Case.Insensitive);
        // Points the model at the runtime param tools rather than baking indices in.
        result.ShouldContain("native_list_channel_plugin_params");
    }

    [Fact]
    public void GetPluginManual_Reverb_ReturnsEffectManualWithChainPlacement()
    {
        string result = Plugin().GetPluginManual("reverb");

        result.ShouldStartWith("OK:");
        result.ShouldContain("native_list_mixer_plugin_params");
        result.ShouldContain("chain", Case.Insensitive);
    }

    [Fact]
    public void GetPluginManual_Unknown_ErrsWithDidYouMeanList()
    {
        string result = Plugin().GetPluginManual("wobblotron-9000");

        result.ShouldStartWith("ERR:");
        result.ShouldContain("3xOSC"); // suggestion list includes the installed manuals
    }

    [Fact]
    public void ListPluginManuals_ListsSeedManualsWithType()
    {
        string result = Plugin().ListPluginManuals();

        result.ShouldStartWith("OK:");
        result.ShouldContain("3xOSC");
        result.ShouldContain("[generator]");
        result.ShouldContain("[effect]");
    }

    [Fact]
    public void Parse_ReadsFrontmatterAndStripsItFromBody()
    {
        var m = PluginManual.Parse(
            "---\nname: Test Synth\naliases: ts, testsynth\ntype: effect\ncategory: Cat\nsummary: A summary.\n---\n# Test Synth\nBody line.",
            fallbackName: "fallback");

        m.Name.ShouldBe("Test Synth");
        m.Type.ShouldBe(ManualType.Effect);
        m.Category.ShouldBe("Cat");
        m.Summary.ShouldBe("A summary.");
        m.Aliases.ShouldContain("testsynth");
        m.Body.ShouldStartWith("# Test Synth");
        m.Body.ShouldNotContain("summary:"); // frontmatter is stripped from the returned body
    }

    [Fact]
    public void Parse_WithoutFrontmatter_FallsBackGracefully()
    {
        var m = PluginManual.Parse("# Just a body, no frontmatter", fallbackName: "myplugin");

        m.Name.ShouldBe("myplugin");
        m.Type.ShouldBe(ManualType.Generator);
        m.Body.ShouldContain("Just a body");
    }

    private sealed class EmptyRetriever : IKnowledgeRetriever
    {
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(string query, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeHit>>(Array.Empty<KnowledgeHit>());
    }
}
