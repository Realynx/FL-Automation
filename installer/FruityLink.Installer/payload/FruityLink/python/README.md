# FruityLink embedded Python

This is the framework's private CPython runtime and MIT-licensed fruitylink Python library.
Plugins such as FL Python IDE execute through FruityLink.Scripting inside FL Studio.
Users do not need to install Python or run pip. This directory is installed even when
FLMCP is deselected. The optional MCP companion retains its legacy paths for compatibility;
both plugins share the same process-owned interpreter, never a second interpreter.

See runtime/LICENSE.txt, FRUITYLINK-LICENSE.txt and RUNTIME-PROVENANCE.json.
