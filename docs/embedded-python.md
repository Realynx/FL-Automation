# Embedded Python

`FruityLink.Scripting.EmbeddedPythonRuntime` executes Python **inside the host
process**, with `fl` connected directly to the same typed SDK dispatcher used by
external scripting clients. It never starts a Python worker or sends a named-pipe
request back into FL. The installer bundles private Windows x64 CPython 3.14.6 and
the `fruitylink-python` wheel; end users do not install Python, pip, or a venv.

```csharp
await using var python = new EmbeddedPythonRuntime(
    new EmbeddedPythonOptions(runtimeDirectory, sdkWheelPath),
    async (method, parameters, cancellation) =>
    {
        try { return await dispatcher.HandleRequestAsync(method, parameters, cancellation); }
        finally { await dispatcher.DrainAsync(); }
    });
JsonElement response = await python.ExecuteAsync(
    "import os\nprint(fl.transport.tempo)\nresult = {'pid': os.getpid()}", 30);
```

Both locations must be absolute paths. A source directory containing `fruitylink`
is also accepted for development. A consumer such as FLMCP applies its project,
path, and session policy in the supplied callback. The standalone named-pipe
plugin is optional for this embedded path.

## Interpreter ownership

The SDK loads the exact private `python314.dll` using a constrained Windows DLL
search and initializes it with the public, opaque `PyInitConfig` API. Initialization
uses isolated settings: no environment configuration, user site packages, signal
handlers, or bytecode writes. Its import paths are `python314.zip`, the private
runtime directory, the SDK wheel or source directory, and the deterministically
ordered wheels installed one per directory beneath
`FruityLink/python/extensions`. A missing extension directory is valid; multiple
wheels in one extension directory are rejected as an ambiguous installation. It
does not read `PYTHONPATH`, alter PATH, scan user directories, or load FL Studio's
own `Shared/Python/python312.dll`.

One dedicated background thread owns the interpreter. It releases the GIL while
idle and while a direct managed SDK callback waits for FL. Scripts are serialized
across all runtime leases. Imported modules persist between scripts, while each
script receives fresh globals and an execution-scoped `fl` object. Retained `fl`
references cannot be used by another script or by another Python thread.

The host shares the Scripting assembly across collectible plugin contexts. Native
function pointers and the interpreter remain resident until FL exits; disabling a
plugin revokes its connection and releases its managed handler after draining. The
SDK never calls `Py_Finalize`, unloads CPython, or terminates FL/Python processes.
Re-enabling a plugin reuses the interpreter. Changing runtime/package locations or
recovering from failed initialization requires restarting FL. An already loaded
`python314.dll` at another path, or an already initialized 3.14 interpreter owned by
another component, is rejected. Different-version Python coexistence is not a
promise of compatibility with arbitrary third-party native extensions.

## Results and cancellation

Scripts use the same `fl` object and typed records as external SDK clients. Assign
`result` to a JSON-compatible value or SDK dataclass. Responses contain
`{ok,result,stdout,stderr,stdoutTruncated,stderrTruncated,error?,traceback?}`. Normal
Python exceptions become `ok:false`; host cancellation throws
`OperationCanceledException`, and an elapsed execution deadline throws
`TimeoutException`, only after active work has drained.

Code is limited to 4 MiB, each captured output stream to 64 KiB, the result to
512 KiB, and the complete response to 1 MiB. Bounds and exception formatting are
shared with the optional external worker. Raw OS writes can bypass Python stream
capture. Deadlines range from 1 to 300 seconds and start when a queued script runs.

### Errors and large results

A script that raises after doing work does not lose that work. The `ok:false`
response carries `error` and `traceback` next to the `stdout`/`stderr` captured up
to the failure, and when the script had already assigned `result` to a serializable
value, that value is returned under `result` with `resultPartial:true`. If the
partial value cannot be serialized, `result` stays `null` and `resultPartialError`
explains why. Successful responses are unchanged, so `ok:true` callers need no
migration; a caller that treated a non-null `result` as proof of success should
check `ok` instead.

```json
{"ok": false, "result": {"tempo": 140.0}, "resultPartial": true,
 "error": "KeyError: 'missing'", "traceback": "Traceback (most recent call last): ...",
 "stdout": "read tempo\n", "stderr": "", "stdoutTruncated": false, "stderrTruncated": false}
```

When the complete response would exceed the 1 MiB bound, the `result` value is
dropped first and the response is returned with `resultDropped:true`, `ok:false`, an
`error` naming the serialized size, and the captured output and traceback intact.
Only when output alone exceeds the bound does the response collapse to
`{ok:false,result:null,error}`. Hosts that show responses to an AI agent (such as
FL MCP) apply their own smaller display budget on top of these limits and save the
full response to a file; see that host's documentation.

Cancellation is cooperative at Python trace and SDK callback boundaries. Nested
`exec`, Python helper functions, and imported Python loops reached from the script
are traced. The first script entry and every SDK call check cancellation immediately.
During Python computation, frame ancestry and the managed cancellation callback are
checked once per 1024 trace events. Tracing remains active between polls; batching
avoids a managed/native crossing for every line in numerical loops. This is an
event-count bound, not a wall-clock deadline or a guarantee against trusted code
that catches cancellation. Cancellation does not interrupt a running FL function, a blocking C
extension, or code that disables tracing. The SDK waits for those calls to return;
it never treats a timeout as permission to unload or terminate active native code.
Initialization also drains before cancellation is acknowledged. Callbacks must
honor this completion contract, as the example's `DrainAsync` barrier does.

The dispatcher opts into the optional `IFlNativeCompletionScope` implemented by
`FlInjectBridge`. Even when a raw in-process call reaches its internal timeout,
the scoped managed call waits for the actual native task before propagating that
timeout. The dispatcher therefore keeps its gate through native completion, and
a batch cannot advance to its next item early. This also covers non-Python tools
using the shared dispatcher. The global transport gate is released before draining
so unrelated host/UI calls can still run; ordinary unscoped SDK callers keep their
bounded waits. Custom control implementations must provide the same completion
guarantee if they detach native work from their returned tasks.

Python `threading.Thread` children are joined before execution completes, while
their output remains captured. They cannot call FL. Raw `_thread` starts are
rejected by an interpreter-local lifecycle audit hook because they cannot be
reliably enumerated and drained. A child that never finishes can keep execution
pending indefinitely. Native extensions with their own unmanaged background work
must provide and honor their own shutdown contract; do not leave such work running
after the script. These controls are lifecycle rules for trusted scripts, not a
security sandbox. In-process native crashes or `os._exit` can terminate FL.

## Validation

A live hidden-launch smoke test on 2026-09-12 also passed against FL Studio 26.1.3.5570.
`fl_project_start` and `fl_status` reported process 47204, and `fl_execute_python`
returned that same PID from `os.getpid()` with CPython 3.14.6, tempo 140 and the
structured Sampler channel record. `fl_project_close` saved the test project and
closed the owned session. This baseline validates startup, direct SDK reads and
save/close; it does not claim every edit or render path has been exercised. Local
evidence is `Fl-MCP/artifacts/live-launch-20260912-170149.json` in the companion repository.

Native integration tests launch a disposable .NET console host, load the real DLL
there, and check Python's PID against the host PID. They exercise isolated imports,
Unicode, the shared operation catalogue, typed notes and tempo callbacks, bounded
output, exceptions, nested/helper-loop cancellation, child-thread drain, scoped
connections, interpreter conflict rejection, and disable/re-enable. No FL process
is started, attached, or modified by these tests.

Set these environment variables before running the SDK C# gate:

```powershell
$env:FRUITYLINK_TEST_PYTHON_RUNTIME = 'C:\absolute\verified-python-runtime'
$env:FRUITYLINK_TEST_PYTHON_PACKAGE = (Resolve-Path .\python\src).Path
$env:FRUITYLINK_TEST_PYTHON = 'C:\absolute\python-for-external-pipe-tests.exe'
dotnet test tests/FruityLink.Scripting.Tests -c Release -warnaserror
```

The runtime must be extracted from the official
[`python-3.14.6-embed-amd64.zip`](https://www.python.org/ftp/python/3.14.6/python-3.14.6-embed-amd64.zip),
SHA256 `df901e84a896ff1ee720ad03377e0c8d8c2244fda79808aeeaff6316df1cb75c`.
The installer owns production distribution and preserves Python's LICENSE and
third-party notices. SDK CI downloads this pinned archive into temporary test
storage and verifies its hash. Tests fail with setup guidance if the runtime is
missing; they do not silently skip native coverage. The package path defaults to
`python/src` if omitted. Test against the built wheel by setting it explicitly.

The implementation follows the official [initialization API](https://docs.python.org/3.14/c-api/init_config.html),
[thread-state/GIL APIs](https://docs.python.org/3.14/c-api/init.html), and
[thread audit events](https://docs.python.org/3.14/library/_thread.html).
