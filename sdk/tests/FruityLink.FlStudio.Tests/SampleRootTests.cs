using FruityLink.Core.Hosting;
using FruityLink.FlStudio.Inject;
using Xunit;

namespace FruityLink.FlStudio.Tests;

/// <summary>
/// The sample search roots and their tags. Live finding 2026-09-18: an agent asked to use the user's own
/// drums found <c>[U]</c> empty, because a personal library reaches FL's browser through
/// <b>Browser extra search folders</b> — stored in the registry under
/// <c>HKCU\Software\Image-Line\FL Studio &lt;major&gt;\Search paths</c> as <c>&lt;folder&gt;,&lt;display name&gt;</c>
/// values — and the SDK only ever searched FL's own two roots.
/// </summary>
public sealed class SampleRootTests : IDisposable
{
    private readonly List<string> temporary = [];

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([Path.Combine(Path.GetTempPath(), "fruitylink-roots-" + Guid.NewGuid().ToString("N")), .. parts]);
        Directory.CreateDirectory(path);
        temporary.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in temporary)
            try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void BrowserFoldersAreTaggedInOrderAndComeBeforeFlsOwnRoots()
    {
        var drums = Folder("Drums");
        var vocals = Folder("Vocals");

        var roots = FlSampleRoots.Compose(@"C:\FL", [drums, vocals]);

        Assert.Equal(["[B1]", "[B2]", "[P]", "[U]"], roots.Select(root => root.Tag));
        Assert.Equal(drums, roots[0].Root);
        Assert.Equal(vocals, roots[1].Root);
        Assert.Equal(Path.Combine(@"C:\FL", "Data", "Patches", "Packs"), roots[2].Root);
    }

    [Fact]
    public void WithoutAnInstallDirectoryOnlyTheUserRootAndTheExtrasRemain()
    {
        var roots = FlSampleRoots.Compose(null, [Folder("Kicks")]);

        Assert.Equal(["[B1]", "[U]"], roots.Select(root => root.Tag));
    }

    [Fact]
    public void UnusableRepeatedAndNestedFoldersAreDropped()
    {
        var library = Folder("Library");
        var inside = Path.Combine(library, "Kicks");
        Directory.CreateDirectory(inside);
        var documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Image-Line", "FL Studio");

        var roots = FlSampleRoots.Compose(null, [library, library, inside, Path.Combine(library, "missing"), "  ", documents]);

        // One tag per distinct usable folder: the repeat, the nested folder, the missing folder, the blank
        // entry and the folder that IS the [U] root would each have listed the same file under two tags.
        Assert.Equal(["[B1]", "[U]"], roots.Select(root => root.Tag));
        Assert.Equal(library, roots[0].Root);
    }

    [Theory]
    [InlineData("nonsense", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AnUnusableSearchPathValueYieldsNoFolder(string? value, string? expected)
        => Assert.Equal(expected, FlSampleRoots.SearchPathFolder(value));

    [Fact]
    public void ASearchPathValueIsSplitAtTheDisplayNameEvenWhenTheFolderHasCommas()
    {
        var plain = Folder("Packs");
        Assert.Equal(plain, FlSampleRoots.SearchPathFolder($"{plain},splice"));
        Assert.Equal(plain, FlSampleRoots.SearchPathFolder($"  {plain} , splice "));
        // A value with no display name at all is still a folder.
        Assert.Equal(plain, FlSampleRoots.SearchPathFolder(plain));

        var comma = Folder("Drums, Loops");
        Assert.Equal(comma, FlSampleRoots.SearchPathFolder($"{comma},my drums"));
    }

    [Fact]
    public void TheEnvironmentVariableAddsFoldersAheadOfEverythingElse()
    {
        var first = Folder("A");
        var second = Folder("B");
        var previous = Environment.GetEnvironmentVariable(FlSampleRoots.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FlSampleRoots.EnvironmentVariable, $"{first};{second}");
            Assert.Equal([first, second], FlSampleRoots.EnvironmentFolders());

            var roots = FlSampleRoots.Discover(null);
            Assert.Equal(first, roots[0].Root);
            Assert.Equal("[B1]", roots[0].Tag);
            Assert.Equal("[U]", roots[^1].Tag);
        }
        finally { Environment.SetEnvironmentVariable(FlSampleRoots.EnvironmentVariable, previous); }
    }

    [Fact]
    public void AnUnsetEnvironmentVariableAddsNothing()
    {
        var previous = Environment.GetEnvironmentVariable(FlSampleRoots.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FlSampleRoots.EnvironmentVariable, null);
            Assert.Empty(FlSampleRoots.EnvironmentFolders());
        }
        finally { Environment.SetEnvironmentVariable(FlSampleRoots.EnvironmentVariable, previous); }
    }

    [Fact]
    public void ReadingFlsBrowserFoldersNeverThrowsAndOnlyReturnsExistingFolders()
    {
        // Whatever this machine has configured, the read is advisory: it must not throw and must not
        // hand back a folder that is not there (the resolver would silently never match it).
        foreach (var folder in FlSampleRoots.BrowserSearchFolders())
            Assert.True(Directory.Exists(folder), folder);
    }

    [Fact]
    public void DescribeSampleSplitsABrowserTaggedEntry()
    {
        var described = FlInjectBridge.DescribeSample(@"[B2]Drums\Kicks\My Kick.WAV");

        Assert.Equal((@"[B2]Drums\Kicks\My Kick.WAV", "[B2]", @"Drums\Kicks\My Kick.WAV", "My Kick", ".wav"),
            (described.Entry, described.RootTag, described.RelativePath, described.Name, described.Extension));
    }
}
