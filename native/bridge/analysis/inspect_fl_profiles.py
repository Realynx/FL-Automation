"""Read-only PE/signature/Delphi VMT evidence; never load or execute the input DLL.

Usage: python inspect_fl_profiles.py engine.dll --signatures ../sigscan.cpp --output report.json
The preferred image base is preserved in VA fields; RVAs are relative to that base.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct


class PeImage:
    def __init__(self, path):
        self.path = Path(path)
        self.data = self.path.read_bytes()
        if self.data[:2] != b"MZ":
            raise ValueError("Not a PE image")
        header = self.unpack("<I", 0x3C)[0]
        if self.data[header:header + 4] != b"PE\0\0":
            raise ValueError("Invalid PE signature")
        count = self.unpack("<H", header + 6)[0]
        optional_size = self.unpack("<H", header + 20)[0]
        if self.unpack("<H", header + 24)[0] != 0x20B:
            raise ValueError("Expected PE32+")
        self.base = self.unpack("<Q", header + 48)[0]
        self.sections = []
        for index in range(count):
            offset = header + 24 + optional_size + index * 40
            size_virtual, rva, size, raw = self.unpack("<IIII", offset + 8)
            flags = self.unpack("<I", offset + 36)[0]
            if size and raw + size > len(self.data):
                raise ValueError("Truncated section")
            self.sections.append({"name": self.data[offset:offset + 8].rstrip(b"\0").decode(),
                                  "rva": rva, "size": size, "raw": raw,
                                  "virtualSize": size_virtual, "flags": flags})

    def unpack(self, fmt, offset):
        return struct.unpack_from(fmt, self.data, offset)

    def file_offset(self, va):
        for section in self.sections:
            delta = va - self.base - section["rva"]
            if 0 <= delta < section["size"]:
                return section["raw"] + delta
        raise ValueError("VA is not backed by file data: " + hex(va))

    def address(self, offset):
        for section in self.sections:
            delta = offset - section["raw"]
            if 0 <= delta < section["size"]:
                return self.base + section["rva"] + delta
        raise ValueError("File offset outside sections")

    def qword(self, va):
        return self.unpack("<Q", self.file_offset(va))[0]

    def executable(self, va):
        return any(s["flags"] & 0x20000000 and
                   s["rva"] <= va - self.base < s["rva"] + s["size"]
                   for s in self.sections)

    def occurrences(self, needle):
        start = 0
        while (start := self.data.find(needle, start)) >= 0:
            try:
                yield self.address(start)
            except ValueError:
                pass
            start += 1


def scan_symbols(image, source):
    expression = re.compile(r'\{\s*"([^"]+)"\s*,\s*"([^"]*)"\s*,\s*'
                            r'(SK_\w+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)')
    results = []
    for name, pattern, kind, displacement, end, displacement_size, delta in expression.findall(source):
        entry = {"name": name, "kind": kind, "pattern": pattern, "matches": []}
        if not pattern:
            results.append(entry)
            continue
        regex = re.compile(b"(?=(" + b"".join(b"." if token.startswith("?") else
                           re.escape(bytes([int(token, 16)])) for token in pattern.split()) + b"))", re.DOTALL)
        for section in image.sections:
            if not section["flags"] & 0x20000000:
                continue
            data = image.data[section["raw"]:section["raw"] + section["size"]]
            for match in regex.finditer(data):
                address = image.base + section["rva"] + match.start()
                resolved = address
                if kind == "SK_DataRef":
                    relative = struct.unpack_from("<i", data, match.start() + int(displacement))[0]
                    resolved += int(end) + relative + int(delta)
                elif kind == "SK_VtableSlot":
                    size = int(displacement_size)
                    resolved = int.from_bytes(data[match.start() + int(displacement):
                                                    match.start() + int(displacement) + size], "little", signed=True)
                entry["matches"].append({"matchVa": hex(address), "resolvedVa": hex(resolved),
                                         "resolvedRva": hex(resolved - image.base)})
        results.append(entry)
    return results


def class_evidence(image, name):
    encoded = name.encode("ascii")
    candidates = []
    for string_va in image.occurrences(bytes([len(encoded)]) + encoded):
        for pointer_va in image.occurrences(struct.pack("<Q", string_va)):
            classref = pointer_va + 0x88
            try:
                if image.qword(classref - 0xC8) != classref:
                    continue
                size = image.qword(classref - 0x80)
                slots = {hex(offset): hex(image.qword(classref + offset))
                         for offset in range(-0x70, 0x390, 8)}
                if not 0x100 <= size <= 0x10000 or not image.executable(image.qword(classref)):
                    continue
                candidates.append({"classNameVa": hex(string_va), "classNamePointerVa": hex(pointer_va),
                                   "classRefVa": hex(classref), "classRefRva": hex(classref - image.base),
                                   "selfPointerVa": hex(classref - 0xC8), "instanceSize": hex(size),
                                   "parentPointerVa": hex(image.qword(classref - 0x78)), "slots": slots})
            except (ValueError, struct.error):
                continue
    return {"name": name, "candidates": candidates}


def published_field_evidence(image, name, selected):
    evidence = class_evidence(image, name)
    for candidate in evidence["candidates"]:
        table = image.qword(int(candidate["classRefVa"], 16) - 0xA0)
        offset = image.file_offset(table)
        count = image.unpack("<H", offset)[0]
        if count > 4096:
            raise ValueError("Implausible published field count")
        offset += 10  # packed UInt16 count followed by a 64-bit class-table pointer
        fields = []
        for _ in range(count):
            field_offset, type_index, length = image.unpack("<IHB", offset)
            end = offset + 7 + length
            if end > len(image.data):
                raise ValueError("Truncated published field")
            field_name = image.data[offset + 7:end].decode("ascii")
            if field_name in selected:
                fields.append({"name": field_name, "offset": hex(field_offset), "typeIndex": type_index})
            offset = end
        candidate["publishedFieldTableVa"] = hex(table)
        candidate["selectedPublishedFields"] = fields
    return evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("engine")
    parser.add_argument("--signatures", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    image = PeImage(args.engine)
    report = {"file": str(image.path), "bytes": len(image.data),
              "sha256": hashlib.sha256(image.data).hexdigest(), "preferredImageBase": hex(image.base),
              "symbols": scan_symbols(image, Path(args.signatures).read_text(encoding="utf-8")),
              "classes": [class_evidence(image, name) for name in ("TScriptDialog", "TVectorForm", "TQuickEdit")],
              "menuForms": [published_field_evidence(image, name, {"MainMenu", "NewMainMenu", "TopToolbar"})
                            for name in ("TFruityLoopsMainForm", "TToolbarForm")]}
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    unique = sum(len(entry["matches"]) == 1 for entry in report["symbols"])
    print(f"{unique}/{len(report['symbols'])} unique signatures; report: {args.output}")


if __name__ == "__main__":
    main()
