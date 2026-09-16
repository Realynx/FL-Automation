using System.Reflection;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Plugins.PythonIde.Execution;
using FruityLink.Scripting;
using Xunit;

namespace FruityLink.Plugins.PythonIde.Execution.Tests;

/// <summary>The test host is disposable; these tests never load FL Studio or its native bridge.</summary>
public sealed class EmbeddedExecutionTests
{
    [Fact(Timeout = 30000)]
    public async Task RealInterpreterRunsInTheTestProcessAndSurvivesStopAndLeaseHandover()
    {
        var options = TestOptions();
        var fl = DispatchProxy.Create<INativeFlControl, ExecutionTests.ControlProxy>();
        await using var ide = new PythonIdeExecutionService(fl, handler => new EmbeddedPythonRuntime(
            EmbeddedPythonRuntimeLocator.Resolve(runtimeDirectory: options.RuntimeDirectory, pythonPackagePath: options.PythonPackagePath), handler));
        await VerifyOutputAndErrorsAsync(ide);
        await VerifyCancellationAndRecoveryAsync(ide);
        await VerifySharedInterpreterAsync(ide, fl, options);
    }

    private static async Task VerifyOutputAndErrorsAsync(IPythonIdeExecution ide)
    {
        var result = await ide.ExecuteAsync("""
            import os, sys, builtins
            builtins._ide_test_sentinel='shared interpreter'
            print('IDE 音楽')
            print('diagnostic', file=sys.stderr)
            result={'pid':os.getpid(),'tempo':fl.ops.get_tempo(),'isolated':sys.flags.isolated}
            """, 10);
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        var value = result.GetProperty("result");
        Assert.Equal(Environment.ProcessId, value.GetProperty("pid").GetInt32());
        Assert.Equal(123, value.GetProperty("tempo").GetDouble());
        Assert.Equal(1, value.GetProperty("isolated").GetInt32());
        Assert.Equal("IDE 音楽\n", result.GetProperty("stdout").GetString());
        Assert.Equal("diagnostic\n", result.GetProperty("stderr").GetString());
        var error = await ide.ExecuteAsync("print('before error')\nraise ValueError('IDE error')", 10);
        Assert.False(error.GetProperty("ok").GetBoolean());
        Assert.Contains("IDE error", error.GetProperty("error").GetString());
        Assert.Contains("ValueError", error.GetProperty("traceback").GetString());
        Assert.Equal("before error\n", error.GetProperty("stdout").GetString());
    }

    private static async Task VerifyCancellationAndRecoveryAsync(IPythonIdeExecution ide)
    {
        await Assert.ThrowsAsync<TimeoutException>(() => ide.ExecuteAsync("while True: pass", 1));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ide.ExecuteAsync("while True: pass", 10, stop.Token));
        Assert.Equal(42, (await ide.ExecuteAsync("result=42", 10)).GetProperty("result").GetInt32());
    }

    private static async Task VerifySharedInterpreterAsync(PythonIdeExecutionService ide, INativeFlControl fl, EmbeddedPythonOptions options)
    {
        // An MCP-style lease starting after the IDE must reuse the process interpreter.
        await using var dispatcher = new FlScriptingDispatcher(fl);
        await using var otherPlugin = new EmbeddedPythonRuntime(options, async (method, parameters, ct) =>
        {
            try { return await dispatcher.HandleRequestAsync(method, parameters, ct); }
            finally { await dispatcher.DrainAsync(); }
        });
        var shared = await otherPlugin.ExecuteAsync("import builtins\nresult=builtins._ide_test_sentinel", 10);
        Assert.Equal("shared interpreter", shared.GetProperty("result").GetString());
        await ide.DisposeAsync();

        // An IDE starting after that plugin takes the active configuration without requiring an install beside its test assembly.
        await using var reopened = new PythonIdeExecutionService(fl);
        Assert.Equal(options, EmbeddedPythonRuntimeLocator.GetActiveOptions());
        var resumed = await reopened.ExecuteAsync("import builtins\nresult=[builtins._ide_test_sentinel,fl.ops.get_tempo()]", 10);
        Assert.Equal("shared interpreter", resumed.GetProperty("result")[0].GetString());
        Assert.Equal(123, resumed.GetProperty("result")[1].GetDouble());
    }

    private static EmbeddedPythonOptions TestOptions()
    {
        var runtime = Environment.GetEnvironmentVariable("FRUITYLINK_TEST_PYTHON_RUNTIME");
        Assert.True(!string.IsNullOrWhiteSpace(runtime),
            "Set FRUITYLINK_TEST_PYTHON_RUNTIME to the private CPython 3.14.6 x64 directory. This test does not launch FL.");
        var package = Environment.GetEnvironmentVariable("FRUITYLINK_TEST_PYTHON_PACKAGE")
            ?? Path.Combine(Path.GetDirectoryName(runtime!)!, "fruitylink_python-0.2.0-py3-none-any.whl");
        return new(Path.GetFullPath(runtime!), Path.GetFullPath(package));
    }
}
