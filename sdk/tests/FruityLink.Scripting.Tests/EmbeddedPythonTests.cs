using System.Diagnostics;
using System.IO.Compression;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class EmbeddedPythonTests
{
    [Theory]
    [InlineData("execution")]
    [InlineData("foreign")]
    public async Task RealInterpreterRunsOnlyInThrowawayHost(string mode)
    {
        string? runtime = Environment.GetEnvironmentVariable("FRUITYLINK_TEST_PYTHON_RUNTIME");
        Assert.True(!string.IsNullOrWhiteSpace(runtime),
            "Set FRUITYLINK_TEST_PYTHON_RUNTIME to the verified CPython 3.14.6 Windows x64 runtime directory; see docs/embedded-python.md.");
        string package = Environment.GetEnvironmentVariable("FRUITYLINK_TEST_PYTHON_PACKAGE") ?? Path.Combine(FindSdkRoot(), "python", "src");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "embedded-test-host", "FruityLink.EmbeddedPython.TestHost.dll"));
        start.ArgumentList.Add(runtime!);
        start.ArgumentList.Add(package);
        start.ArgumentList.Add(mode);
        string? extensionWheel = null;
        if (mode == "execution")
        {
            extensionWheel = Path.Combine(Path.GetTempPath(), $"fruitylink-extension-{Guid.NewGuid():N}.whl");
            using (var archive = ZipFile.Open(extensionWheel, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("fixture_extension.py").Open()))
                writer.Write("VALUE = 'extension import passed'\n");
            start.ArgumentList.Add(extensionWheel);
        }
        start.Environment["PYTHONPATH"] = @"C:\nonexistent-ambient-python";
        start.Environment["PYTHONHOME"] = @"C:\nonexistent-ambient-home";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Embedded interpreter test host did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (extensionWheel is not null) File.Delete(extensionWheel);
        }
        Assert.True(process.ExitCode == 0, $"Embedded interpreter integration ({mode}) failed.\n{await output}\n{await errors}");
        Assert.Contains("Embedded Python integration passed.", await output);
        Assert.Equal("", await errors);
    }

    [Fact]
    public async Task LeaseValidationAndDisposalDoNotInitializePython()
    {
        Assert.Throws<ArgumentException>(() => new EmbeddedPythonRuntime(new("relative", "relative"), (_, _, _) => Task.FromResult<object?>(null)));
        await using var runtime = new EmbeddedPythonRuntime(new(Path.GetTempPath(), Path.GetTempPath()), (_, _, _) => Task.FromResult<object?>(null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.ExecuteAsync("", 0));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(new string('x', 4 * 1024 * 1024 + 1), 10));
        await runtime.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.ExecuteAsync("result=1", 10));
    }

    private static string FindSdkRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FruityLink.Sdk.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("SDK source root not found for embedded Python tests.");
    }
}
