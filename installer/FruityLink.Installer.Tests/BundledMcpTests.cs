using FruityLink.Installer.Cli;
using FruityLink.Installer.Core;
using Xunit;

namespace FruityLink.Installer.Tests;

public sealed class BundledMcpTests
{
    private const string FlPath = @"C:\TestInstallations\FL Studio 2026";
    private const string PluginDestination = "FruityLink/plugins/fl-mcp/FlMcp.Plugin.dll";
    private const string ServerDestination = "FruityLink/tools/fl-mcp/server/FlMcp.Server.exe";
    private const string WheelDestination = "FruityLink/tools/fl-mcp/python/fruitylink_python-0.2.0-py3-none-any.whl";

    [Fact]
    public void AvailableBundle_IsIncludedByDefaultWithBothRequiredComponents()
    {
        using var payload = new PayloadFixture(bundleAvailable: true);
        var basis = InstallManifest.Default();

        var selected = BundledMcp.Select(basis, payload.Root);

        Assert.True(BundledMcp.IsAvailable(payload.Root));
        Assert.Equal(basis.Items.Count + 2, selected.Items.Count);
        AssertComponent(selected, "plugin", "FruityLink/plugins/fl-mcp");
        AssertComponent(selected, "companion", "FruityLink/tools/fl-mcp");
    }

    [Fact]
    public void Deselection_ExcludesPluginAndCompanionFromActualInstall()
    {
        using var payload = new PayloadFixture(bundleAvailable: true);
        var basis = InstallManifest.Default();
        var selected = BundledMcp.Select(basis, payload.Root, include: false);
        var engine = new InstallEngine(payload.FileSystem, "test");

        var plan = engine.PlanInstall(FlPath, selected, payload.Root, out var errors);
        Assert.Empty(errors);
        var install = engine.ExecuteInstall(plan, FlPath, dryRun: false, new NullLog());

        Assert.True(install.Success);
        Assert.Equal(basis.ToJson(), selected.ToJson());
        Assert.True(payload.FileSystem.FileExists(Installed("FruityLink/FlBridge.dll")));
        Assert.False(payload.FileSystem.FileExists(Installed(PluginDestination)));
        Assert.False(payload.FileSystem.FileExists(Installed(ServerDestination)));
        Assert.False(payload.FileSystem.FileExists(Installed(WheelDestination)));
        Assert.DoesNotContain(install.Record!.FilesWritten, IsMcpDestination);
    }

    [Fact]
    public void Selection_ClonesTheManifestWithoutAccumulatingItemsAcrossSelections()
    {
        using var payload = new PayloadFixture(bundleAvailable: true);
        var basis = InstallManifest.Default();
        var original = basis.ToJson();

        var first = BundledMcp.Select(basis, payload.Root);
        var second = BundledMcp.Select(basis, payload.Root);
        var excluded = BundledMcp.Select(basis, payload.Root, include: false);
        first.Items[0].Description = "Changed only in this selection";

        Assert.Equal(original, basis.ToJson());
        Assert.Equal(original, excluded.ToJson());
        Assert.Equal(basis.Items.Count + 2, first.Items.Count);
        Assert.Equal(basis.Items.Count + 2, second.Items.Count);
        Assert.NotSame(basis, first);
        Assert.NotSame(basis.Items, first.Items);
        Assert.NotSame(basis.Items[0], first.Items[0]);
        Assert.NotEqual(first.Items[0].Description, second.Items[0].Description);
    }

    [Fact]
    public void MissingBundle_KeepsOlderPayloadInstallable()
    {
        using var payload = new PayloadFixture(bundleAvailable: false);
        var basis = InstallManifest.Default();
        var selected = BundledMcp.Select(basis, payload.Root);
        var engine = new InstallEngine(payload.FileSystem, "test");

        var plan = engine.PlanInstall(FlPath, selected, payload.Root, out var errors);

        Assert.False(BundledMcp.IsAvailable(payload.Root));
        Assert.Equal(basis.ToJson(), selected.ToJson());
        Assert.NotSame(basis, selected);
        Assert.Empty(errors);
        Assert.True(engine.ExecuteInstall(plan, FlPath, dryRun: false, new NullLog()).Success);
    }

    [Fact]
    public void SelectedBundle_IsRecordedAndUninstalledWhileUnrelatedFilesSurvive()
    {
        using var payload = new PayloadFixture(bundleAvailable: true);
        var manifest = BundledMcp.Select(InstallManifest.Default(), payload.Root);
        var engine = new InstallEngine(payload.FileSystem, "test");
        var plan = engine.PlanInstall(FlPath, manifest, payload.Root, out var errors);
        Assert.Empty(errors);
        Assert.True(engine.ExecuteInstall(plan, FlPath, dryRun: false, new NullLog()).Success);

        var record = engine.TryLoadRecord(FlPath, manifest);
        Assert.NotNull(record);
        AssertInstalledAndRecorded(payload.FileSystem, record, PluginDestination, "MCP PLUGIN");
        AssertInstalledAndRecorded(payload.FileSystem, record, ServerDestination, "MCP SERVER");
        AssertInstalledAndRecorded(payload.FileSystem, record, WheelDestination, "PYTHON WHEEL");
        var userFile = Installed("FruityLink/tools/fl-mcp/user-script.py");
        payload.FileSystem.WriteAllText(userFile, "USER CONTENT");

        var uninstall = engine.PlanUninstall(FlPath, manifest, record, new NullLog());
        Assert.True(engine.ExecuteUninstall(uninstall, dryRun: false, new NullLog()).Success);

        Assert.False(payload.FileSystem.FileExists(Installed(PluginDestination)));
        Assert.False(payload.FileSystem.FileExists(Installed(ServerDestination)));
        Assert.False(payload.FileSystem.FileExists(Installed(WheelDestination)));
        Assert.False(payload.FileSystem.FileExists(Path.Combine(FlPath, manifest.RecordFileName)));
        Assert.Equal("USER CONTENT", payload.FileSystem.ReadAllText(userFile));
        Assert.Equal("ORIGINAL", payload.FileSystem.ReadAllText(Path.Combine(FlPath, "version.dll")));
        Assert.Equal("FL", payload.FileSystem.ReadAllText(Path.Combine(FlPath, "FL64.exe")));
    }

    [Fact]
    public void Cli_DefaultIncludesMcpAndExplicitOptOutIsRecognized()
    {
        var defaults = CliOptions.Parse(["--install", "--silent"]);
        var excluded = CliOptions.Parse(["--install", "--silent", "--without-mcp"]);

        Assert.False(defaults.WithoutMcp);
        Assert.True(excluded.WithoutMcp);
        Assert.True(excluded.Install);
        Assert.True(excluded.Silent);
        Assert.Empty(excluded.Unknown);
    }

    private static void AssertComponent(InstallManifest manifest, string source, string destination)
    {
        var item = Assert.Single(manifest.Items, item => item.Destination == destination);
        Assert.Equal(Path.Combine(BundledMcp.SourceDirectory, source), item.Source);
        Assert.Equal(PayloadKind.ManagedDir, item.Kind);
        Assert.True(item.IsDirectory);
        Assert.True(item.Required);
    }

    private static void AssertInstalledAndRecorded(InMemoryFileSystem fs, InstallRecord record,
        string relativePath, string expectedContent)
    {
        var path = Installed(relativePath);
        Assert.Equal(expectedContent, fs.ReadAllText(path));
        Assert.Contains(record.FilesWritten, written =>
            Path.GetFullPath(written).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMcpDestination(string path) =>
        path.Replace('\\', '/').Contains("/fl-mcp/", StringComparison.OrdinalIgnoreCase);

    private static string Installed(string relativePath) => Path.Combine(FlPath, relativePath);

    private sealed class NullLog : IProgressLog
    {
        public void Log(LogLevel level, string message) { }
    }

    private sealed class PayloadFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("FruityLink.BundledMcpTests-").FullName;
        public InMemoryFileSystem FileSystem { get; } = new();

        public PayloadFixture(bool bundleAvailable)
        {
            FileSystem.WriteAllText(Path.Combine(FlPath, "FL64.exe"), "FL");
            FileSystem.WriteAllText(Path.Combine(FlPath, "version.dll"), "ORIGINAL");
            WritePayload("version.dll", "PROXY");
            WritePayload("FruityLink/FlBridge.dll", "BRIDGE");
            if (!bundleAvailable) return;

            // BundledMcp availability uses real Directory.Exists. Payload contents and all
            // install/uninstall mutations still use the in-memory file system exclusively.
            Directory.CreateDirectory(Path.Combine(Root, BundledMcp.SourceDirectory));
            WritePayload(BundledMcp.SourceDirectory + "/plugin/FlMcp.Plugin.dll", "MCP PLUGIN");
            WritePayload(BundledMcp.SourceDirectory + "/companion/server/FlMcp.Server.exe", "MCP SERVER");
            WritePayload(BundledMcp.SourceDirectory + "/companion/python/fruitylink_python-0.2.0-py3-none-any.whl", "PYTHON WHEEL");
        }

        private void WritePayload(string relativePath, string content) =>
            FileSystem.WriteAllText(Path.Combine(Root, relativePath), content);

        public void Dispose()
        {
            var bundle = Path.Combine(Root, BundledMcp.SourceDirectory);
            if (Directory.Exists(bundle)) Directory.Delete(bundle);
            var optional = Path.Combine(Root, "optional-plugins");
            if (Directory.Exists(optional)) Directory.Delete(optional);
            Directory.Delete(Root);
        }
    }
}
