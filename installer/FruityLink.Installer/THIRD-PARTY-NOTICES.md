# Installer dependency notices

The installer uses **Tomlyn 2.10.1** to read and preserve TOML configuration when connecting
selected MCP clients. Tomlyn is Copyright (c) 2019-2026 Alexandre Mutel, under BSD-2-Clause.
The complete upstream notice and disclaimer are reproduced in
[licenses/Tomlyn-2.10.1-LICENSE.txt](licenses/Tomlyn-2.10.1-LICENSE.txt).

Source: https://github.com/xoofx/Tomlyn/tree/2.10.1

License source: https://raw.githubusercontent.com/xoofx/Tomlyn/2.10.1/license.txt

The optional FLMCP component retains its own LICENSE and THIRD-PARTY-NOTICES.md in the
installed companion. Its private CPython runtime retains the official LICENSE.txt, including
bundled third-party notices, under python/runtime/. Runtime provenance is recorded under
python/RUNTIME-PROVENANCE.json. These components retain their licenses independently of the
framework and installer.

The base framework also includes CPython and the MIT-licensed FruityLink Python library
under `FruityLink/python/`, independently of MCP selection. Their full notices and runtime
provenance accompany those files. FL Python IDE is a separate MIT-licensed plugin using
AvaloniaEdit. Its `THIRD-PARTY-NOTICES.md` and `licenses/` directory cover the editor and
shared UI toolkit dependencies; a copy is retained with the framework's notices even when
the editor is not selected.
