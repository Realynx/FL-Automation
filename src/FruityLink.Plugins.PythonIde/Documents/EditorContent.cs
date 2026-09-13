namespace FruityLink.Plugins.PythonIde.Documents;

internal static class EditorContent
{
    public const string Starter = """
        # FL Python IDE · runs inside this FL Studio process
        # `fl` is already connected to the current project.
        import os

        print(f"Hello from FL Studio (process {os.getpid()})")
        print(f"Tempo: {fl.transport.tempo} BPM")

        # Assign `result` to display structured output below.
        result = fl.project.info

        # Changes are immediate. Save your FL project before experimenting.
        # fl.transport.tempo = 128
        """;

    public const string Help = """
        CONNECTED TO THIS PROJECT
        `fl` is available in every run.
        No connect() or MCP needed.

        EXPLORE
        fl.project.info
        fl.channels.list()
        fl.patterns.list()
        fl.timebase.ppq
        fl.capabilities()

        USE THE SDK
        fl.transport.tempo = 128
        fl.transport.play()
        fl.transport.stop()

        Set `result = ...` for structured
        output. Use print() for messages.
        Browse API shows operation
        arguments and return schemas.

        SHORTCUTS
        Ctrl+N    New script
        Ctrl+O    Open script
        Ctrl+S    Save script
        F5 / Ctrl+Enter  Run all
        Shift+Enter   Run selection
        Shift+F5      Stop

        EXECUTION
        Each run gets fresh globals.
        Imported modules stay loaded.
        Selection must be valid Python.
        Stop waits for active FL/native
        work; it cannot interrupt that
        work safely. No automatic undo.
        """;
}
