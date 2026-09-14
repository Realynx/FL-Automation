# Configure FLMCP through the framework installer

The installer includes private CPython 3.14.6 x64 and fruitylink-python 0.2.0. FLMCP loads
the runtime into the connected FL Studio process, where user Python scripts execute with
the SDK available. No separate Python installation, pip/venv setup, or PATH change is needed.
Installation launches python/runtime/python.exe only to check SDK imports.

## Select clients during installation

1. Leave the FLMCP component checked. Its files are selected by default; client checkboxes
   start unchecked so only the AI apps you choose have their settings changed.
2. Check the clients to connect. Choices include Codex, Claude Desktop (standard and detected
   Microsoft Store installations), Claude Code, Cursor, VS Code's default profile, Gemini CLI,
   Windsurf, and OpenCode. Choose Other client / generic-json for a stdio JSON export.
3. Use the selected FL installation's saved Empty.flp template, or provide another absolute
   saved FLP path. The default workspace is the original user's local application data under
   FlMcp/Projects. The installer preserves this user context across elevation.
4. Install. Selected-client setup writes their FLMCP entries with backups of existing settings,
   preserves unrelated configuration, and enables fl-mcp in the user's persisted plugin state.
   If no clients are selected, the installer copies the component but does not enable it or
   change any client settings. Client tool approvals remain under your control.
5. Restart selected clients to load the server and restart FL after installing updated files.
   Choose an existing session with fl_instances and fl_attach, or close other FL instances
   before fl_project_start to create a new managed project.

## Connect an existing FL Studio session

Enable FLMCP in that running FL instance's Plugins menu. The installer's saved enabled-state
applies to subsequent launches; changing client settings does not enable a plugin in an
already running process. Use fl_instances to list sessions for the configured FL installation,
then fl_attach with the chosen process ID. Untitled projects are supported. If a project switch
is detected, reattach before continuing; project identity checks are best effort.

Use fl_detach to disconnect. Detaching or exiting the AI client leaves an attached FL process
open. In attached mode, saving creates a fresh workspace copy without replacing the current
project; fl_project_close and fl_project_render are unavailable. Use a managed project for that workflow.
Attaching itself does not require the starting template, but installer setup validates the
template because the same configuration also supports managed project creation.

The installed server is server/FlMcp.Server.exe. Its FL_MCP_PYTHON_RUNTIME setting points to
this companion's python/runtime directory, and FL_MCP_PYTHON_PATH points to
python/fruitylink_python-0.2.0-py3-none-any.whl. Keep the companion directory together.
The plugin and shared host must use matching FruityLink 0.2.0 contracts.

## Configure or retry later

Run the framework installer again to select clients, or use its own CLI:

    FruityLink.Installer.exe --list-mcp-clients
    FruityLink.Installer.exe --configure-mcp --fl-path "C:\Program Files\Image-Line\FL Studio 2026" --mcp-clients codex --dry-run

Remove --dry-run to apply the configuration. --mcp-template and --mcp-workspace override the
default saved project template and output workspace. --mcp-python-runtime can select another
compatible private CPython 3.14.6 x64 runtime directory. No client CLI or registration helper is
required. If setup reports malformed settings or missing files, resolve the reported error
and retry --configure-mcp; do not overwrite the user's configuration manually.

Uninstall removes matching connections only for selected clients and this FL installation.
Existing configuration backups are retained. Check the installer's per-client result before
closing it; copying the plugin files and connecting an AI app are separate reported steps.

FLMCP still requires a licensed FL Studio installation, a normal Windows desktop session,
and the companion's .NET 10 runtime. Python bundling does not prove live authoring or rendering.
Follow docs/live-verification.md using a disposable project. FLMCP retains its PolyForm
Noncommercial 1.0.0 license; SDK, Python, and dependency notices retain their own terms.
Python scripts run with FL Studio's permissions and share its process. This is not a security
sandbox; completed project edits are not automatically rolled back on errors or cancellation.