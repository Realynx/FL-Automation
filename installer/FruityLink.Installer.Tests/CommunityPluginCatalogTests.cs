using FruityLink.Installer.Core;
using Xunit;

namespace FruityLink.Installer.Tests;

public class CommunityPluginCatalogTests
{
    [Fact]
    public void Parse_ReadsValidEntries()
    {
        var plugins = CommunityPluginCatalog.Parse("""
        {
          "version": 1,
          "plugins": [
            {
              "id": "midi-tools",
              "name": "MIDI Tools",
              "description": "Batch MIDI utilities",
              "version": "1.2.0",
              "downloadUrl": "https://github.com/example/midi-tools/releases/download/v1.2.0/midi-tools.zip"
            }
          ]
        }
        """);

        var plugin = Assert.Single(plugins);
        Assert.Equal("midi-tools", plugin.Id);
        Assert.Equal("MIDI Tools", plugin.Name);
        Assert.Equal("1.2.0", plugin.Version);
    }

    [Theory]
    // Path traversal / unsafe folder names must never become plugins\<id>.
    [InlineData("../evil", "https://example.com/a.zip")]
    [InlineData("a\\b", "https://example.com/a.zip")]
    [InlineData("", "https://example.com/a.zip")]
    // Downloads must be HTTPS.
    [InlineData("ok-id", "http://example.com/a.zip")]
    [InlineData("ok-id", "file:///C:/a.zip")]
    [InlineData("ok-id", "not a url")]
    public void Parse_DropsUnsafeEntries(string id, string url)
    {
        var plugins = CommunityPluginCatalog.Parse($$"""
        { "version": 1, "plugins": [ { "id": "{{id}}", "name": "X", "downloadUrl": "{{url}}" } ] }
        """);

        Assert.Empty(plugins);
    }

    [Fact]
    public void Parse_DropsDuplicateIds_KeepsFirst()
    {
        var plugins = CommunityPluginCatalog.Parse("""
        { "version": 1, "plugins": [
            { "id": "dup", "name": "First",  "downloadUrl": "https://example.com/1.zip" },
            { "id": "DUP", "name": "Second", "downloadUrl": "https://example.com/2.zip" }
        ] }
        """);

        var plugin = Assert.Single(plugins);
        Assert.Equal("First", plugin.Name);
    }

    [Fact]
    public void Parse_EmptyOrMissingPluginList_YieldsEmpty()
    {
        Assert.Empty(CommunityPluginCatalog.Parse("""{ "version": 1 }"""));
        Assert.Empty(CommunityPluginCatalog.Parse("""{ "version": 1, "plugins": [] }"""));
    }

    [Fact]
    public void ToPayloadItem_TargetsPluginsFolder_WithAbsoluteSource()
    {
        var plugin = new CommunityPlugin { Id = "midi-tools", Name = "MIDI Tools", Version = "1.0" };
        var item = CommunityPluginCatalog.ToPayloadItem(plugin, @"C:\temp\stage\midi-tools");

        Assert.True(item.IsDirectory);
        Assert.Equal(@"C:\temp\stage\midi-tools", item.Source);
        Assert.Equal(@"FruityLink\plugins\midi-tools", item.Destination);
        // Absolute sources pass through ResolveSource untouched — that's what staging relies on.
        Assert.Equal(@"C:\temp\stage\midi-tools", InstallEngine.ResolveSource(item, @"C:\ignored\payload"));
    }
}
