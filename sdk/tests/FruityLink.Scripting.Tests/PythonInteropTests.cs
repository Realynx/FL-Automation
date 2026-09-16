using FruityLink.Core.Abstractions;
using System.Diagnostics;
using Xunit;

namespace FruityLink.Scripting.Tests;

public sealed class PythonInteropTests
{
    [Fact]
    public async Task PythonSdkUsesTheActualAuthenticatedDotNetEndpoint()
    {
        await using var fixture = new PipeFixture(structured: true);
        double tempo = 120;
        fixture.Recorder.Handler = (method, arguments) => method.Name switch
        {
            "SetTempoAsync" => SetTempo((double)arguments[0]!),
            "GetTempoAsync" => Task.FromResult(tempo),
            "QueryProjectAsync" => Task.FromResult(new FlProjectInfo("Interop project", @"C:\Projects\interop.flp", false)),
            _ => RecordingControl.DefaultReturn(method)
        };
        await fixture.Server.StartAsync();
        string sdk = FindSdkRoot();
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("FRUITYLINK_TEST_PYTHON") ?? "python",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(sdk, "python", "tests", "interop_smoke.py"));
        start.ArgumentList.Add(fixture.DiscoveryPath);
        start.Environment["PYTHONPATH"] = Path.Combine(sdk, "python", "src");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Python test process did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 0, $"Python interop failed.\n{await output}\n{await errors}");
        Assert.Equal(127, tempo);
        var call = Assert.Single(fixture.Recorder.Calls, value => value.Method == "AddNotesAsync");
        var note = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<NoteSpec>>(call.Arguments[1]));
        Assert.Equal(new NoteSpec(0, 60, 0, 96, 100), note);

        Task SetTempo(double value) { tempo = value; return Task.CompletedTask; }
    }

    private static string FindSdkRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "FruityLink.Sdk.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("SDK source root not found for Python integration test.");
    }
}
