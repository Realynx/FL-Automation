# Blender MCP research and FL Python design

Reviewed on 2026-09-12. The likely reference is [ahujasid/blender-mcp](https://github.com/ahujasid/blender-mcp), independently maintained rather than an official Blender project. The inspected revision is [5f8ddaf6e987c4aa0c3467fcc548838b28f64477](https://github.com/ahujasid/blender-mcp/commit/5f8ddaf6e987c4aa0c3467fcc548838b28f64477), committed 2026-09-07. Its [license at that revision](https://github.com/ahujasid/blender-mcp/blob/5f8ddaf6e987c4aa0c3467fcc548838b28f64477/LICENSE) is MIT. No implementation code, assets, branding, telemetry, or external asset integrations were copied.

## What makes its workflow effective

The [MCP server](https://github.com/ahujasid/blender-mcp/blob/5f8ddaf6e987c4aa0c3467fcc548838b28f64477/src/blender_mcp/server.py#L555) exposes `execute_blender_code` and relays an `execute_code` request to the addon. This lets one script perform an intentional sequence instead of requiring a separate MCP tool for every Blender operator.

The [addon execution handler](https://github.com/ahujasid/blender-mcp/blob/5f8ddaf6e987c4aa0c3467fcc548838b28f64477/addon.py#L1367) supplies `bpy`, executes the code, and returns captured stdout. Its separate scene summary and named-object detail methods let a model inspect context, write a focused script, and verify the resulting state. Socket requests enter a queue; a timer drains that queue on Blender's main thread.

This matches Blender's own distinction between [context](https://docs.blender.org/api/main/bpy.context.html), application data, and operators. Blender's [timer documentation](https://docs.blender.org/api/4.2/bpy.app.timers.html#use-a-timer-to-react-to-events-in-another-thread) describes queuing work from other threads for application-side execution.

## Application to FL Studio

The reusable Python API belongs in the FruityLink SDK plugin-system repository, where it can serve scripts, other MCP servers, notebooks, and non-MCP automation. FL MCP embeds private CPython inside the FL process and exposes the shared `FruityLink.Scripting` dispatcher through direct managed callbacks. The typed `fl` object provides public control methods and structured queries; native dispatch remains inside the established FruityLink bridge.

FL MCP consumes that library through three additions: Python execution, API discovery, and concise usage documentation. The existing tools remain compatibility facades. Their DAW calls use the same shared dispatcher, rather than establishing a second native operation catalogue.

Python executes inside FL on the SDK's dedicated interpreter thread, with `fl` backed by an in-process callback. A script can print progress and assign a JSON-compatible `result`. stdout, stderr, traceback, and structured results are bounded and returned in the bridge response. There is no Python subprocess, per-script response file, or callback pipe. A process-wide shared SDK assembly owns the interpreter across plugin disable/re-enable; CPython is initialized lazily and is not finalized while FL is alive.

The companion retains project ownership, launch, snapshot, and render lifecycle. It sends one authenticated `python_execute` bridge request while holding the managed-session gate. The plugin rechecks project identity for direct SDK callbacks without reacquiring that gate or calling through its own bridge. Cancellation requests cooperative interruption and keeps the gate until Python and active native calls drain. A lost bridge acknowledgement leaves FL running until later serialized status confirms execution has finished.

Embedding means Python shares FL's address space and privileges. Unsafe native extensions or process-exit calls can crash or terminate FL; the script API is not a sandbox. A blocking native call cannot be forcibly cancelled safely, and completed edits are not rolled back. The private runtime is separate from FL's bundled Python installation. FL still requires its licensed Windows desktop installation; embedding does not create a supported service-mode host.
