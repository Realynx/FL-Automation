using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class EmbeddedPythonRuntimeLocatorTests
{
    private static readonly string Host = Path.Combine(Path.GetTempPath(), "fruitylink-locator-tests", "FruityLink");
    private static readonly EmbeddedPythonOptions Common = Under(Path.Combine(Host, "python"));
    private static readonly EmbeddedPythonOptions Legacy = Under(Path.Combine(Host, "tools", "fl-mcp", "python"));

    [Fact]
    public void IdeOnlyInstallUsesFrameworkRuntimeWithoutSystemPythonDiscovery()
    {
        var readVariables = new List<string>();
        var options = EmbeddedPythonRuntimeLocator.ResolveCore(Host, null, variable => { readVariables.Add(variable); return null; }, _ => false);
        Assert.Equal(Common, options);
        Assert.DoesNotContain("PATH", readVariables);
        Assert.DoesNotContain("PYTHONHOME", readVariables);
    }

    [Fact]
    public void IdeStartingFirstChoosesInstalledLegacyMcpRuntimeSoBothAgree()
    {
        var first = Resolve(null, path => path == Path.Combine(Legacy.RuntimeDirectory, "python314.dll") || path == Legacy.PythonPackagePath);
        Assert.Equal(Legacy, first);
        var later = Resolve(first, _ => true, new() { ["FL_MCP_PYTHON_RUNTIME"] = Legacy.RuntimeDirectory, ["FL_MCP_PYTHON_PATH"] = Legacy.PythonPackagePath });
        Assert.Same(first, later);
    }

    [Fact]
    public void McpStartingFirstPinsRuntimeForIdeEvenIfCommonBundleExists()
    {
        Assert.Same(Legacy, Resolve(Legacy, _ => true));
    }

    [Fact]
    public void IncompleteLegacyInstallationDoesNotOverrideCommonRuntime()
    {
        Assert.Equal(Common, Resolve(null, path => path == Path.Combine(Legacy.RuntimeDirectory, "python314.dll")));
    }

    [Fact]
    public void FrameworkEnvironmentTakesPrecedenceOverLegacyEnvironment()
    {
        var environment = new Dictionary<string, string?>
        {
            ["FRUITYLINK_PYTHON_RUNTIME"] = Common.RuntimeDirectory, ["FRUITYLINK_PYTHON_PATH"] = Common.PythonPackagePath,
            ["FL_MCP_PYTHON_RUNTIME"] = Legacy.RuntimeDirectory, ["FL_MCP_PYTHON_PATH"] = Legacy.PythonPackagePath,
        };
        Assert.Equal(Common, Resolve(null, _ => true, environment));
    }

    [Fact]
    public void ExplicitConflictingOverrideCannotReplacePinnedInterpreter()
    {
        Assert.Throws<InvalidOperationException>(() => Resolve(Common, _ => true,
            new() { ["FRUITYLINK_PYTHON_RUNTIME"] = Legacy.RuntimeDirectory }));
    }

    [Fact]
    public void EquivalentOverrideReturnsThePinnedCanonicalOptions()
    {
        var options = Resolve(Legacy, _ => false, new() { ["FL_MCP_PYTHON_RUNTIME"] = Legacy.RuntimeDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar });
        Assert.Same(Legacy, options);
    }

    [Fact]
    public void MethodOverridesTakePrecedenceAndRejectRelativePaths()
    {
        var resolved = EmbeddedPythonRuntimeLocator.ResolveCore(Host, null, _ => "relative-env", _ => false,
            Legacy.RuntimeDirectory, Legacy.PythonPackagePath);
        Assert.Equal(Legacy, resolved);
        Assert.Throws<ArgumentException>(() => EmbeddedPythonRuntimeLocator.ResolveCore(Host, null, _ => null, _ => false, "relative"));
        Assert.Throws<ArgumentException>(() => EmbeddedPythonRuntimeLocator.ResolveCore("relative", null, _ => null, _ => false));
    }

    private static EmbeddedPythonOptions Resolve(EmbeddedPythonOptions? active, Func<string, bool> exists,
        Dictionary<string, string?>? environment = null) => EmbeddedPythonRuntimeLocator.ResolveCore(Host, active,
        key => environment?.GetValueOrDefault(key), exists);

    private static EmbeddedPythonOptions Under(string directory) => new(Path.Combine(directory, "runtime"),
        Path.Combine(directory, "fruitylink_python-0.2.0-py3-none-any.whl"));
}
