# FruityLink Python

Typed Python automation for FL Studio: compose patterns, arrange clips, route audio, create automation and analyze WAV/PCM data.

**[Getting started](../docs/python/getting-started.md)** · **[API guide](../docs/python/api.md)** · **[Examples](../docs/python/examples.md)** · **[Execution modes](../docs/python/execution.md)**

FLMCP and the optional Python IDE supply an already connected `fl` using the bundled interpreter. External programs need Windows, Python 3.11+, and the matching **FL Python** endpoint plugin enabled in FL Studio. The package uses only the standard library.

For independent build jobs, use [`launch(..., background=True)`](../docs/background-launching.md#standalone-python-builds). The framework's SessionHost launches FL on a private Windows desktop and exposes its scripting endpoint without requiring an MCP or FL Python plugin. Each job has its own working project, process and desktop; save explicitly before closing, or use `session.render()` to save a snapshot and export WAV.

From the FL-Automation repository root:

```powershell
python -m pip install ./python
```

```python
from fruitylink import connect

with connect() as fl:
    print(fl.project.info)
    print(fl.transport.tempo)
```

Use `connect(pid=...)` when several FL instances have discovery records. Edits affect the active project immediately; batches do not provide rollback. Read the [SDK overview](../docs/python/index.md) for the object model and [index/unit conventions](../docs/python/api.md#indices-and-units) before writing edits.

## Development

From the repository root:

```powershell
uv sync --directory python
uv run --directory python python tools/check.py
```

The gate checks generated contract parity, Ruff, strict mypy, tests and package build. After changing the C# contract, run `python tools/generate_operations.py` from the `python` directory. See [development documentation](../docs/development.md) for the repository-wide checks.

The package is licensed under the repository's MIT license.
