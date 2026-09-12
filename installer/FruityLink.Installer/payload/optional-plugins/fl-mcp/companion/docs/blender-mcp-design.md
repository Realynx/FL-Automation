# Blender MCP research and FL Python design

Reviewed on 2026-09-12. The likely reference is [ahujasid/blender-mcp](https://github.com/ahujasid/blender-mcp), independently maintained rather than an official Blender project. The inspected revision is [5f8ddaf6e987c4aa0c3467fcc548838b28f64477](https://github.com/ahujasid/blender-mcp/commit/5f8ddaf6e987c4aa0c3467fcc548838b28f64477), committed 2026-09-07. Its [license at that revision](https://github.com/ahujasid/blender-mcp/blob/5f8ddaf6e987c4aa0c3467fcc548838b28f64477/LICENSE) is MIT. No implementation code, assets, branding, telemetry, or external asset integrations were copied.

## What makes its workflow effective

The [MCP server](https://github.com/ahujasid/blender-mcp/blob/5f8ddaf6e987c4aa0c3467fcc548838b28f64477/src/blender_mcp/server.py#L555) exposes `execute_blender_code` and relays an `execute_code` request to the addon. This lets one script perform an intentional sequence instead of requiring a separate MCP tool for every Blender operator.

The [addon execution handler](https://github.com/ahujasid/blender-mcp/blob/5f8ddaf6e987c4aa0c3467fcc548838b28f64477/addon.py#L1367) supplies `bpy`, executes the code, and returns captured stdout. Its separate scene summary and named-object detail methods let a model inspect context, write a focused script, and verify the resulting state. Socket requests enter a queue; a timer drains that queue on Blender's main thread.

This matches Blender's own distinction between [context](https://docs.blender.org/api/main/bpy.context.html), application data, and operators. Blender's [timer documentation](https://docs.blender.org/api/4.2/bpy.app.timers.html#use-a-timer-to-react-to-events-in-another-thread) describes queuing work from other threads for application-side execution.

## Application to FL Studio

FL Studio has no equivalent embedded `bpy` API in this repository. The reusable Python API belongs in the FruityLink SDK plugin-system repository, where it can serve scripts, other MCP servers, notebooks, and non-MCP automation. It calls the shared `FruityLink.Scripting` dispatcher, which exposes only typed public FL control methods and structured queries. Native dispatch remains inside the established FruityLink bridge.

FL MCP consumes that library through three additions: Python execution, API discovery, and concise usage documentation. The existing tools remain compatibility facades. Their DAW calls use the same shared dispatcher, rather than establishing a second native operation catalogue.

Python executes in a disposable external worker with `fl` already connected. A script can print progress and assign a JSON-compatible `result`. stdout, stderr, traceback, and structured results are bounded and kept separate from MCP stdout. A private response file carries the result so user printing cannot corrupt the protocol. A Windows job owns the worker and its descendants; code is delivered only after the job is assigned.

The companion retains its existing project ownership, launch, snapshot, and render lifecycle. During a script it holds the managed-session gate once. An authenticated, same-user pipe relay checks the owned project's identity and invokes the shared dispatcher without trying to reacquire that gate. This prevents a script-to-FL callback from deadlocking behind its own execution request. The relay is removed when the script ends.

This is process isolation for cancellation and lifecycle control, not a security sandbox for arbitrary Python. Scripts have the user's ordinary Python filesystem/network permissions. Completed FL edits are not rolled back by cancellation. FL still requires its licensed Windows desktop installation; an external Python worker does not create a supported service-mode FL host.
