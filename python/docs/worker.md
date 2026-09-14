# Isolated Python execution worker

This optional command-line utility is for external Python clients. FLMCP uses the
SDK's [embedded runtime](../../docs/embedded-python.md) inside FL Studio instead.

Launch `python -m fruitylink.worker --response PRIVATE_RESPONSE_PATH`. The worker
blocks on one JSON line from stdin before connecting or executing code. Optional
`--request PRIVATE_REQUEST_PATH` reads the same JSON from a file.

```json
{"code":"print(fl.transport.tempo)\nresult = fl.project.info","endpoint":{"apiVersion":1,"pid":1234,"instanceId":"instance-uuid","pipeName":"FruityLinkScripting-1234","token":"private-token"}}
```

Endpoint is optional for local discovery. A relay can add `serverPid`. User code
sees `fl` (Studio) and can assign `result`; scripts may import `fruitylink` and any
installed Python package. Execution is in this Python process, never FL Studio's
interpreter. Lifecycle/render workflows remain the embedding application's concern.
`timeoutSeconds` optionally sets each SDK request's timeout (1–300 seconds,
default 30). It is not a whole-script deadline; the launcher enforces that separately.

The caller owns private request/response paths, credentials, working directory,
timeout, and process-tree termination. Start the process suspended or attach it to
the intended process group/job before supplying stdin when cancellation must cover
descendant processes. Worker credentials should not appear in process arguments.

The response is written atomically to the dedicated file:
`{ok,result,stdout,stderr,stdoutTruncated,stderrTruncated,error?,traceback?}`.
Normal Python output is captured with 64 KiB per stream; traceback/error are bounded.
Result must be JSON-compatible or an SDK dataclass, ≤512 KiB. The entire response
is ≤1 MiB; request is ≤4 MiB. Unsupported results produce `ok:false` with a useful
error. Both script exceptions and `SystemExit` produce responses. Process crashes,
`os._exit`, and forced termination may leave no response; callers must detect that.

A script that raises after assigning `result` still returns that value under
`result` with `resultPartial:true` (or `resultPartialError` when it cannot be
serialized), together with the output captured before the failure. A response over
1 MiB drops the `result` value first (`resultDropped:true`, `ok:false`) and keeps
the streams, error and traceback.

The response file separates control data from print output. Raw OS writes and
child-process output can bypass Python redirects; the launcher must separately
drain, cap, or discard process stdout/stderr. The worker is **not a security sandbox**:
scripts have ordinary user permissions and can access files and the network.
Cancellation is not rollback; completed DAW edits remain.
