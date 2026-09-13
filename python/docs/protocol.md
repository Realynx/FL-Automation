# Scripting connection protocol

The v1 plugin endpoint is a local Windows named pipe. Direct discovery reads
`%LOCALAPPDATA%/FruityLink/scripting/{pid}.json` containing `apiVersion:1`, `pid`,
`instanceId`, `pipeName:"FruityLinkScripting-{pid}"`, `token`, and `createdAt`.
Discovery never silently chooses among multiple records. A stale record fails
the live handshake. Remove stale files only after confirming their process exited.

Each request opens one connection, verifies the OS pipe server PID, writes one
frame, reads one response, then closes. Framing is a four-byte unsigned
little-endian UTF-8 JSON length followed by that many bytes. Maximum: 4 MiB.
No pipelining. Timeouts cancel outstanding overlapped I/O and close the connection.
Closing can stop queued work; an already running native operation may complete.

```json
{"id":"unique-request-id","token":"private-token","method":"invoke","params":{"operation":"set_tempo","arguments":{"bpm":120}}}
```

Responses have the same `id` and either `result` (including null) or
`error:{code,message,data?}`. Methods are `catalog`, `capabilities`, `invoke`,
and `batch`. Capabilities must echo `apiVersion`, `pid`, and `instanceId`.

Batch params: `{operations:[{operation,arguments}],stopOnError:true}` with at most
256 operations. Result: `{results:[{operation,result,error}],stoppedOnError}`.
Each operation runs in order; failure never implies rollback. A response failure
must not be retried automatically because earlier mutations may have completed.

`RequestTransport.request(method, params)` is the injection interface: return the
unwrapped JSON result or raise. `Studio(transport)` trusts the caller's identity
policy. A relay endpoint can supply `serverPid` when the pipe server process is
different from the target FL Studio `pid`; handshake still verifies target identity.
Tokens are excluded from endpoint reprs and client-generated error messages.

Native compatibility and memory offsets stay in the SDK's selected version
profile. Python consumes capabilities and operation contracts, without a separate
FL-version switch or guessed offsets.
