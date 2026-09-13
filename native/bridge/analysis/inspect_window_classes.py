"""Read-only Delphi form metadata; emits focused Ghidra targets for native hosting."""
import argparse
import hashlib
import json
from pathlib import Path
from inspect_fl_profiles import PeImage, class_evidence, scan_symbols


def fields(image, table):
    if not table:
        return []
    offset = image.file_offset(table)
    count = image.unpack("<H", offset)[0]
    if count > 4096:
        return []
    offset += 10
    result = []
    for _ in range(count):
        field, kind, length = image.unpack("<IHB", offset)
        result.append({"name": image.data[offset + 7:offset + 7 + length].decode("ascii"),
                       "offset": hex(field), "kind": kind})
        offset += 7 + length
    return result


def methods(image, table):
    if not table:
        return []
    offset = image.file_offset(table)
    count = image.unpack("<H", offset)[0]
    if count > 4096:
        return []
    offset += 2
    result = []
    for _ in range(count):
        length, address, name_length = image.unpack("<HQB", offset)
        if length < 11:
            break
        result.append({"name": image.data[offset + 11:offset + 11 + name_length].decode("ascii"),
                       "va": hex(address)})
        offset += length
    return result


def paint_handlers(image, table):
    if not table:
        return {}
    offset = image.file_offset(table)
    count = image.unpack("<H", offset)[0]
    if count > 4096:
        raise ValueError("Unreasonable Delphi dynamic method count")
    result = {}
    for index in range(count):
        message = image.unpack("<H", offset + 2 + index * 2)[0]
        if message not in (0x0F, 0x14, 0x318):
            continue
        address = image.unpack("<Q", offset + 2 + count * 2 + index * 8)[0]
        if image.executable(address):
            result[hex(message)] = hex(address)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("engine")
    parser.add_argument("output")
    parser.add_argument("--signatures", required=True)
    args = parser.parse_args()
    image = PeImage(args.engine)
    result = {"sha256": hashlib.sha256(image.data).hexdigest(), "classes": [], "symbols": []}
    targets = {}
    for name in ("TCustomWPForm", "TWPForm", "TVectorForm", "TScriptDialog", "TBridgedEditorForm", "TFLBaseVectorForm"):
        for candidate in class_evidence(image, name)["candidates"]:
            ref = int(candidate["classRefVa"], 16)
            candidate["name"] = name
            candidate["fields"] = fields(image, image.qword(ref - 0xA0))
            candidate["methods"] = methods(image, image.qword(ref - 0x98))
            candidate["paintHandlers"] = paint_handlers(image, image.qword(ref - 0x90))
            parent = image.qword(image.qword(ref - 0x78))
            candidate["parentClassRef"] = hex(parent)
            for method in candidate["methods"]:
                targets.setdefault(int(method["va"], 16), name + "_" + method["name"])
            for message, address in candidate["paintHandlers"].items():
                targets.setdefault(int(address, 16), name + "_message_" + message)
            candidate["overrides"] = {}
            for offset in range(-0x70, 0x390, 8):
                method = image.qword(ref + offset)
                if not image.executable(method) or image.qword(parent + offset) == method:
                    continue
                candidate["overrides"][hex(offset)] = hex(method)
                if name in ("TCustomWPForm", "TVectorForm", "TScriptDialog", "TBridgedEditorForm", "TFLBaseVectorForm"):
                    targets.setdefault(method, name + "_vmt_" + hex(offset).replace("-", "minus"))
            result["classes"].append(candidate)
    wanted = {"FLui_CreateFormFromClassRef", "FLapp_SetTitle", "FLwp_SetButtonCaption", "FLwp_SetVisible",
              "FLui_ControlSetVisible", "FLui_WP_GetHandle", "FLui_DockLayout", "FLui_Focusable", "FLwp_SetWindowState", "FLui_ZOrderRefresh"}
    for symbol in scan_symbols(image, Path(args.signatures).read_text(encoding="utf-8")):
        if symbol["name"] not in wanted:
            continue
        result["symbols"].append(symbol)
        if len(symbol["matches"]) == 1:
            targets[int(symbol["matches"][0]["resolvedVa"], 16)] = symbol["name"]
    output = Path(args.output)
    output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    output.with_suffix(".tsv").write_text("".join(f"{name}\t{hex(address)}\n" for address, name in targets.items()), encoding="utf-8")
    print(f"{len(result['classes'])} classes; {len(targets)} targets; {output}")


if __name__ == "__main__":
    main()
