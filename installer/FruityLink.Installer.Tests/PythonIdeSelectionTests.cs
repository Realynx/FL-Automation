using FruityLink.Installer.Cli;
using FruityLink.Installer.Core;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class PythonIdeSelectionTests : IDisposable
{
    private readonly string _payload = Path.Combine(Path.GetTempPath(), "fruitylink-ide-test-" + Guid.NewGuid().ToString("N"));

    public PythonIdeSelectionTests()
    {
        Directory.CreateDirectory(Path.Combine(_payload, BundledPythonIde.SourceDirectory));
        Directory.CreateDirectory(Path.Combine(_payload, BundledMcp.SourceDirectory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EditorAndMcpAreIndependentAndKeepFramework(bool editor, bool mcp)
    {
        var basis = InstallManifest.Default();
        string original = basis.ToJson();
        var selected = BundledPythonIde.Select(BundledMcp.Select(basis, _payload, mcp), _payload, editor);

        Assert.Equal(original, basis.ToJson());
        Assert.Contains(selected.Items, item => item.Source == "FruityLink");
        Assert.Equal(editor, selected.Items.Any(item => item.Source == BundledPythonIde.SourceDirectory));
        Assert.Equal(mcp, selected.Items.Any(item => item.Destination == "FruityLink/plugins/fl-mcp"));
    }

    [Fact]
    public void LegacyPayloadWithoutEditorRemainsUsable()
    {
        var basis = InstallManifest.Default();
        Assert.Equal(basis.ToJson(), BundledPythonIde.Select(basis, Path.Combine(_payload, "old")).ToJson());
    }

    [Fact]
    public void CliCanDeselectOnlyOnePlugin()
    {
        var editorOnly = CliOptions.Parse(["--install", "--without-mcp"]);
        Assert.True(editorOnly.WithoutMcp);
        Assert.False(editorOnly.WithoutPythonIde);
        var mcpOnly = CliOptions.Parse(["--install", "--without-python-ide"]);
        Assert.False(mcpOnly.WithoutMcp);
        Assert.True(mcpOnly.WithoutPythonIde);
    }

    public void Dispose() => Directory.Delete(_payload, recursive: true);
}
