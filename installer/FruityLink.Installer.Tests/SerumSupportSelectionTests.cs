using FruityLink.Installer.Cli;
using FruityLink.Installer.Core;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class SerumSupportSelectionTests : IDisposable
{
    private readonly string _payload = Path.Combine(Path.GetTempPath(), "fruitylink-serum-installer-test-" + Guid.NewGuid().ToString("N"));

    public SerumSupportSelectionTests() =>
        Directory.CreateDirectory(Path.Combine(_payload, BundledSerumSupport.SourceDirectory));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectionIsIndependentAndDoesNotMutateBasis(bool include)
    {
        var basis = InstallManifest.Default();
        string original = basis.ToJson();
        var selected = BundledSerumSupport.Select(basis, _payload, include);

        Assert.Equal(original, basis.ToJson());
        Assert.Equal(include, selected.Items.Any(item =>
            item.Source == BundledSerumSupport.SourceDirectory &&
            item.Destination == "FruityLink/python/extensions/serum-support"));
    }

    [Fact]
    public void CliIncludesSerumSupportUnlessExplicitlyDeselected()
    {
        Assert.False(CliOptions.Parse(["--install"]).WithoutSerumSupport);
        Assert.True(CliOptions.Parse(["--install", "--without-serum-support"]).WithoutSerumSupport);
    }

    [Fact]
    public void DestinationUsesAStableExtensionDirectoryAcrossWheelVersions()
    {
        var selected = BundledSerumSupport.Select(InstallManifest.Default(), _payload);
        var item = Assert.Single(selected.Items, item => item.Source == BundledSerumSupport.SourceDirectory);
        Assert.Equal("FruityLink/python/extensions/serum-support", item.Destination);
    }

    [Fact]
    public void MissingLegacyPayloadDoesNotAddARequiredItem()
    {
        var basis = InstallManifest.Default();
        Assert.Equal(basis.ToJson(), BundledSerumSupport.Select(basis, Path.Combine(_payload, "old")).ToJson());
    }

    public void Dispose() => Directory.Delete(_payload, recursive: true);
}
