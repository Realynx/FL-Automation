"""Generate concrete typed operation methods from the SDK's authoritative C# contract."""

import argparse
import html
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = ROOT.parent / "src/FruityLink.Core/Abstractions/INativeFlControl.cs"
OUTPUT = ROOT / "src/fruitylink/operations.py"
SCALARS = {"int": "int", "long": "int", "double": "float", "bool": "bool", "string": "str",
           "FlAutomationTarget": "AutomationTarget", "FlAutomationPointSpec": "AutomationPointSpec",
           "FlAutomationClipResult": "AutomationClipResult"}
RECORDS = ["ClipMove", "ClipResize", "NoteEdit", "NoteRef", "NoteSpec", "PatternClipSpec"]


def snake_case(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name.removesuffix("Async")).lower()


def python_type(native: str) -> str:
    if native.endswith("?"):
        return python_type(native[:-1]) + " | None"
    if native.startswith("IReadOnlyList<"):
        return f"Sequence[{python_type(native[14:-1])}]"
    return SCALARS.get(native, native)


def parameter(source: str) -> tuple[str, str]:
    kind, name, *default = source.strip().split()
    annotation = f"{snake_case(name)}: {python_type(kind)}"
    if default:
        value = {"null": "None", "true": "True", "false": "False"}.get(default[-1], default[-1])
        annotation += f" = {value}"
    return name, annotation


def generate() -> str:
    source = CONTRACT.read_text(encoding="utf-8")
    declarations = re.findall(r"Task(?:<([^\n]+?)>)?\s+(\w+Async)\(([^;]+?)\);", source)
    header = '''"""Generated from INativeFlControl.cs; regenerate with tools/generate_operations.py.

Python arguments use snake_case; generated mappings preserve native wire names.
Native integer scales and index conventions are documented by each operation.
"""

from collections.abc import Sequence
from typing import cast

from .automation_records import AutomationClipResult, AutomationPointSpec, AutomationTarget
from .models import decode_record
from .queries import QueryOperations
from .records import ClipMove, ClipResize, NoteEdit, NoteRef, NoteSpec, PatternClipSpec
from .transport import RequestTransport
from .values import JsonValue, wire_arguments


class Operations(QueryOperations):
    def __init__(self, transport: RequestTransport) -> None:
        self._transport = transport

    def invoke(self, operation: str, **arguments: object) -> JsonValue:
        """Invoke a catalogue operation, including capability-dependent extensions."""
        return self._transport.request("invoke", {"operation": operation, "arguments": wire_arguments(arguments)})
'''
    methods = []
    for native_result, native_name, native_parameters in declarations:
        params = [parameter(p) for p in native_parameters.split(",") if "CancellationToken" not in p]
        name = snake_case(native_name)
        result = python_type(native_result) if native_result else "None"
        signature = ", *, " + ", ".join(p[1] for p in params) if params else ""
        args = ", **{" + ", ".join(f'"{p[0]}": {snake_case(p[0])}' for p in params) + "}" if params else ""
        previous = source[:source.index(f" {native_name}(")]
        summaries = re.findall(r"<summary>(.*?)</summary>", previous, re.DOTALL)
        summary = re.sub(r'<(?:see|paramref) (?:cref|name)="([^"]+)"\s*/>', r"\1", summaries[-1])
        doc = html.unescape(re.sub(r"\s+", " ", re.sub(r"///|<[^>]*>", "", summary))).strip()
        doc = doc.replace('"""', "'''")
        body = f'self.invoke("{name}"{args})'
        if native_result == "FlAutomationClipResult":
            body = f"return decode_record(AutomationClipResult, {body})"
        else:
            body = f"return cast({result}, {body})" if native_result else body
        methods.append(f'\n    def {name}(self{signature}) -> {result}:\n        """{doc}"""\n        {body}\n')
    if len(methods) < 123:
        raise ValueError("Native contract parser did not discover the complete operation surface.")
    return header + "".join(methods)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    expected = generate()
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != expected:
            raise SystemExit("Generated Operations are stale; run tools/generate_operations.py.")
    else:
        OUTPUT.write_text(expected, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
