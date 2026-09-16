# Execution and sessions

The SDK provides synchronous project operations. The application hosting Python decides how an FL session is created, selected, saved, rendered and closed.

## Embedded scripts

FLMCP and the optional Python IDE use `EmbeddedPythonRuntime` inside the FL process. The installer supplies an embedded CPython runtime and the SDK package. An execution receives an already connected `fl`, so scripts can start with:

```python
print(fl.transport.tempo)
result = fl.project.info
```

Each run starts with fresh script globals. Imported modules remain loaded in a shared interpreter, and scripts execute serially. The injected `fl` belongs to that execution: do not retain it for a later run or use it from another Python thread.

The IDE is under active development. Its current editor offers script execution and
result/output views. It is optional; the SDK and MCP workflows do not depend on it
being complete.

### Results and output

Assign a JSON-compatible value or SDK dataclass to `result`. Return summaries or pages instead of complete parameter collections, analysis objects or sample arrays.

| Value | Bound for embedded and worker responses |
| --- | --- |
| Captured `stdout` | 64 KiB |
| Captured `stderr` | 64 KiB |
| Serialized `result` | 512 KiB |
| Complete response | 1 MiB |

Responses report `ok`, `result`, `stdout`, `stderr` and stream truncation flags, with error/traceback details on failure. A result serialization failure does not undo project edits that already ran.

### Cancellation

Embedded deadlines are cooperative and range from 1 to 300 seconds; the deadline starts when a queued script runs. Cancellation is checked at Python trace and SDK callback boundaries. It does not forcibly interrupt an active FL operation, blocking native extension, or code that disables tracing. Completion waits for active work to drain.

Python child threads cannot call FL and are joined before the execution completes. A child that never finishes can keep a run pending. Scripts execute with the host process's ordinary OS permissions; the runtime is for trusted scripts and is not a sandbox.

See [embedded runtime internals](../embedded-python.md) for interpreter ownership, initialization and the managed completion contract.

## External programs

`connect(pid=None, *, endpoint=None, transport=None, timeout=30.0)` returns a `Studio`.

- With no selection, discovery requires exactly one endpoint record and verifies its live identity.
- `pid` selects the discovery record for a specific FL process.
- `endpoint` supplies a known `Endpoint`; a simultaneous `pid` must match it.
- `transport` supplies a caller-managed `RequestTransport` and cannot be combined with `pid` or `endpoint`. Your application owns its identity policy.
- `timeout` configures the named-pipe client's request timeout. It is not a deadline for the whole Python program.

The named-pipe client opens one connection per request. `Studio.close()` and exiting its context manager do not close FL or an injected relay. Ordinary Python globals and imports follow your own process's lifetime.

Discovery files in `%LOCALAPPDATA%\FruityLink\scripting` contain authentication credentials. Keep them private. If a request times out after an edit started, the native edit may still finish; inspect the current project before retrying.

For a custom relay, implement `RequestTransport.request(method, params)` to return the unwrapped JSON result or raise an exception. See [the protocol](../python-protocol.md) for framing, server identity, schemas and batching.

## Optional execution worker

`python -m fruitylink.worker` runs one supplied script in an external Python process. It is a runner for application integrations; it is not the runtime used by FLMCP. The caller owns credentials, private request/response paths, working directory and process lifecycle.

Read [the worker contract](../../python/docs/worker.md) when implementing a launcher. Its SDK request timeout does not implement a whole-script deadline, and cancellation never rolls back completed DAW edits.

## Project and render ownership

`fl.project.open(...)` and save methods affect the connected application's project. They do not establish MCP session ownership, launch a new FL process or implement an unattended render workflow. `fl.project.open_export_dialog(...)` opens the interactive export dialog; it does not verify that a render completed.

Use FLMCP's documented project and render tools for those managed workflows. Use direct SDK operations when your own application provides the surrounding session policy.
