# Installer-supplied Python runtime

This installer includes private CPython 3.14.6 for Windows x64. No user Python,
pip, virtual environment, PATH changes, or package download is required at installation.
The installer sets FL_MCP_PYTHON_RUNTIME to this companion's python/runtime directory and
FL_MCP_PYTHON_PATH to the bundled SDK wheel. FLMCP loads this CPython runtime inside the
connected FL Studio process; user scripts execute there. Python.exe is used only for the
installer's SDK import check, not to execute user scripts.

Official source: https://www.python.org/ftp/python/3.14.6/python-3.14.6-embed-amd64.zip
Official release/checksum: https://www.python.org/downloads/release/python-3146/
Archive SHA256: df901e84a896ff1ee720ad03377e0c8d8c2244fda79808aeeaff6316df1cb75c
Original byte length: 12570832
Embedding documentation: https://docs.python.org/3.14/using/windows.html#the-embeddable-package

Every file from the official ZIP is retained, including runtime/LICENSE.txt with Python's
license and bundled third-party notices. Only runtime/python314._pth is changed:

    python314.zip
    .
    ../fruitylink_python-0.2.0-py3-none-any.whl

These paths are relative to the runtime directory. The standard-library ZIP and native extension
modules stay beside python.exe; the pure-Python SDK wheel is imported directly from its ZIP.
Site initialization remains disabled. PYTHONPATH, user site-packages, and registry Python
configuration do not supply this runtime's imports. Scripts run with FL Studio's OS permissions,
inside its process; this is not a security sandbox. Do not replace these paths with a global
Python installation. Cancellation cannot safely force-stop arbitrary native Python extensions;
follow the embedded execution guidance in the MCP documentation.

RUNTIME-PROVENANCE.json records this pin and the exact SDK wheel hash. The companion's regenerated
SHA256SUMS.json covers installed files; SOURCE-SHA256SUMS.json preserves the original MCP
distribution manifest before the installer added Python. SOURCE-README.md describes the
standalone source deployment; this installer supplies its runtime without user setup.