using FruityLink.Core.Abstractions;
using FruityLink.Scripting;
using FruityLink.Scripting.Tests;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FruityLink.EmbeddedPython.TestHost;

internal static class EmbeddedChecks
{
    internal static async Task RunAsync(EmbeddedPythonOptions options, string mode)
    {
        if (mode == "foreign") { await CheckForeignOwnerAsync(options); return; }
        var (control, recorder) = RecordingControl.Create<ICompleteControl>();
        await using var dispatcher = new FlScriptingDispatcher(control);
        Func<string, JsonElement, CancellationToken, Task<object?>> handler = async (method, parameters, ct) =>
        {
            try { return await dispatcher.HandleRequestAsync(method, parameters, ct); }
            finally { await dispatcher.DrainAsync(); }
        };
        await using var runtime = new EmbeddedPythonRuntime(options, handler);
        await CheckIdentityAndSdkAsync(runtime, recorder, options.ExtensionPackagePaths.Count);
        await CheckErrorsAndLimitsAsync(runtime);
        await CheckCancellationAsync(runtime);
        await CheckThreadDrainAsync(runtime);
        await CheckRetainedReferencesAsync(options, runtime, handler);
        await CheckNativeDrainAsync(options);
    }

    private static async Task CheckIdentityAndSdkAsync(EmbeddedPythonRuntime runtime, RecordingControl recorder, int extensionCount)
    {
        var result = await runtime.ExecuteAsync("""
            import os, sys, ctypes, asyncio
            print('音楽 ♫ café')
            result={'pid':os.getpid(),'isolated':sys.flags.isolated,'env':sys.flags.ignore_environment,
                    'site':sys.flags.no_site,'path':sys.path,'tempo':fl.ops.get_tempo(),
                    'count':len(fl.catalog()['operations'])}
            """, 10);
        Require(result.GetProperty("ok").GetBoolean(), result.ToString());
        var value = result.GetProperty("result");
        Require(value.GetProperty("pid").GetInt32() == Environment.ProcessId, "Python must execute in the host PID.");
        Require(value.GetProperty("isolated").GetInt32() == 1 && value.GetProperty("env").GetInt32() == 1 &&
            value.GetProperty("site").GetInt32() == 1, "Python must ignore ambient environment/site packages.");
        Require(value.GetProperty("path").GetArrayLength() == 3 + extensionCount, "Python should expose only configured isolated import paths.");
        int expectedOperations = typeof(INativeFlControl).GetMethods().Length + typeof(IFlStructuredQuery).GetMethods().Length;
        Require(value.GetProperty("count").GetInt32() == expectedOperations, "Expected complete typed SDK catalogue.");
        Require(value.GetProperty("tempo").GetDouble() == 120, "SDK callback returned incorrect tempo.");
        Require(result.GetProperty("stdout").GetString() == "音楽 ♫ café\n", "Unicode output was corrupted.");
        if (extensionCount != 0)
        {
            result = await runtime.ExecuteAsync("import fixture_extension\nresult=fixture_extension.VALUE", 10);
            Require(result.GetProperty("result").GetString() == "extension import passed", "Configured extension wheel was not importable.");
        }
        result = await runtime.ExecuteAsync("""
            from fruitylink import NoteSpec
            fl.ops.set_tempo(bpm=123)
            fl.ops.add_notes(pattern=1, notes=[NoteSpec(0,60,0,96,100)])
            result=NoteSpec(0,60,0,96,100)
            """, 10);
        Require(result.GetProperty("ok").GetBoolean(), result.ToString());
        Require(recorder.Calls.Any(call => call.Method == nameof(INativeFlControl.AddNotesAsync)), "Typed note operation never reached SDK.");
        Require(result.GetProperty("result").GetProperty("lengthTick").GetInt32() == 96, "Typed record result failed.");
    }

    private static async Task CheckErrorsAndLimitsAsync(EmbeddedPythonRuntime runtime)
    {
        var error = await runtime.ExecuteAsync("print('before')\nraise ValueError('bad 音')", 10);
        Require(!error.GetProperty("ok").GetBoolean() && error.GetProperty("error").GetString()!.Contains("bad 音"), "Python error was lost.");
        Require(error.GetProperty("stdout").GetString() == "before\n", "Output before failure was lost.");
        var partial = await runtime.ExecuteAsync("result={'tempo': fl.ops.get_tempo()}\nraise ValueError('late')", 10);
        Require(!partial.GetProperty("ok").GetBoolean() && partial.GetProperty("resultPartial").GetBoolean() &&
            partial.GetProperty("result").GetProperty("tempo").GetDouble() == 120, "Partial result before failure was lost.");
        var output = await runtime.ExecuteAsync("print('♫'*100000)\nresult=1", 10);
        Require(output.GetProperty("stdoutTruncated").GetBoolean(), "Unbounded Python stdout.");
        var invalid = await runtime.ExecuteAsync("result=object()", 10);
        Require(!invalid.GetProperty("ok").GetBoolean(), "Unserializable result accepted.");
    }

    private static async Task CheckCancellationAsync(EmbeddedPythonRuntime runtime)
    {
        await ExpectAsync<TimeoutException>(() => runtime.ExecuteAsync("while True: pass", 1));
        await ExpectAsync<TimeoutException>(() => runtime.ExecuteAsync("exec('while True: pass')", 1));
        await ExpectAsync<TimeoutException>(() => runtime.ExecuteAsync("""
            import types
            helper=types.ModuleType('temporary_loop')
            exec('def loop():\n    while True: pass', helper.__dict__)
            helper.loop()
            """, 1));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await ExpectAsync<OperationCanceledException>(() => runtime.ExecuteAsync("while True: pass", 10, stop.Token));
        var after = await runtime.ExecuteAsync("print('restored')\nresult=42", 10);
        Require(after.GetProperty("result").GetInt32() == 42 && after.GetProperty("stdout").GetString() == "restored\n", "Interpreter did not recover after cancellation.");
    }

    private static async Task CheckRetainedReferencesAsync(EmbeddedPythonOptions options, EmbeddedPythonRuntime runtime,
        Func<string, JsonElement, CancellationToken, Task<object?>> handler)
    {
        await runtime.ExecuteAsync("import builtins\nbuiltins._test_retained_fl=fl", 10);
        await using var next = new EmbeddedPythonRuntime(options, handler);
        var stale = await next.ExecuteAsync("import builtins\nbuiltins._test_retained_fl.ops.get_tempo()", 10);
        Require(!stale.GetProperty("ok").GetBoolean(), "Retained connection was able to access a later script.");
        await runtime.DisposeAsync();
        await ExpectAsync<ObjectDisposedException>(() => runtime.ExecuteAsync("result=0", 10));
        var reused = await next.ExecuteAsync("result=43", 10);
        Require(reused.GetProperty("result").GetInt32() == 43, "Disabling one lease destroyed the interpreter.");
    }

    private static async Task CheckNativeDrainAsync(EmbeddedPythonOptions options)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runtime = new EmbeddedPythonRuntime(options, async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return 120;
        });
        var call = runtime.ExecuteAsync("result=fl.ops.get_tempo()", 1);
        await entered.Task;
        await Task.Delay(1200);
        Require(!call.IsCompleted, "Timeout acknowledged while a native handler was still active.");
        var dispose = runtime.DisposeAsync().AsTask();
        await Task.Delay(50);
        Require(!dispose.IsCompleted, "Disposal completed before active handler drained.");
        release.SetResult();
        await ExpectAsync<OperationCanceledException>(() => call);
        await dispose;
        await using var reenabled = new EmbeddedPythonRuntime(options, (_, _, _) => Task.FromResult<object?>(null));
        var result = await reenabled.ExecuteAsync("result=44", 10);
        Require(result.GetProperty("result").GetInt32() == 44, "Re-enable after draining failed.");
    }

    private static async Task CheckThreadDrainAsync(EmbeddedPythonRuntime runtime)
    {
        var watch = Stopwatch.StartNew();
        var background = await runtime.ExecuteAsync("""
            import threading, time
            def background():
                time.sleep(0.25)
                print('child finished')
            threading.Thread(target=background).start()
            result=5
            """, 10);
        Require(watch.ElapsedMilliseconds >= 200, "Execution returned before its Python child thread drained.");
        Require(background.GetProperty("stdout").GetString() == "child finished\n", "Child-thread output escaped capture.");
        var denied = await runtime.ExecuteAsync("""
            import threading
            errors=[]
            def mutate():
                try: fl.ops.set_tempo(bpm=140)
                except Exception as error: errors.append(str(error))
            child=threading.Thread(target=mutate)
            child.start()
            child.join()
            result=errors
            """, 10);
        Require(denied.GetProperty("result").GetArrayLength() == 1, "A background thread accessed FL.");
        var raw = await runtime.ExecuteAsync("import _thread\n_thread.start_new_thread(lambda: None, ())", 10);
        Require(!raw.GetProperty("ok").GetBoolean() && raw.GetProperty("error").GetString()!.Contains("raw _thread"),
            "Untracked Python thread creation was accepted.");
        await ExpectAsync<TimeoutException>(() => runtime.ExecuteAsync("""
            import threading
            def loop():
                while True: pass
            threading.Thread(target=loop).start()
            """, 1));
    }

    private static async Task CheckForeignOwnerAsync(EmbeddedPythonOptions options)
    {
        nint library = NativeLibrary.Load(Path.Combine(options.RuntimeDirectory, "python314.dll"));
        var initialize = Marshal.GetDelegateForFunctionPointer<InitializeCall>(NativeLibrary.GetExport(library, "Py_Initialize"));
        initialize();
        await using var runtime = new EmbeddedPythonRuntime(options, (_, _, _) => Task.FromResult<object?>(null));
        var error = await ExpectAsync<InvalidOperationException>(() => runtime.ExecuteAsync("result=1", 10));
        Require(error.Message.Contains("already initialized"), "An external interpreter owner was not rejected.");
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InitializeCall();
}
