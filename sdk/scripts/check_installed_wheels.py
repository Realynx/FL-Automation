"""Compare the wheels installed under a FruityLink tree against this source tree.

`fruitylink-python` and `fruitylink-serum` are installed as version-stamped wheel files that FL's
embedded interpreter imports straight off `sys.path`. The file name never changes while the
version string stays the same, so a wheel built from older sources keeps working, keeps reporting
the same version, and silently removes whatever the newer sources added. That failure mode is not
visible from inside FL: `import fruitylink_serum` succeeds and only the missing attribute shows up,
as an `AttributeError` several layers down (live 2026-09-17: `fruitylink_serum.loading`'s automation
scan raised `type object 'AutomationTarget' has no attribute 'from_event_id'` because the installed
`fruitylink-python` 0.2.0 wheel predated it, while the source tree's 0.2.0 has it).

Run this after packaging and after installing, and rebuild the wheels when it reports a difference:

    python sdk/scripts/check_installed_wheels.py "C:/Program Files/Image-Line/FL Studio 2026/FruityLink"

Source files are compared with line endings normalised, because the wheels are built on a checkout
whose text files may use either convention. Exits non-zero listing every stale, missing or extra
module.
"""

import sys
import zipfile
from pathlib import Path

SDK = Path(__file__).resolve().parent.parent
# package top-level -> the source directory whose files the wheel must carry verbatim.
PACKAGES = {
    "fruitylink": SDK / "python" / "src" / "fruitylink",
    "fruitylink_serum": SDK / "extensions" / "serum-support" / "src" / "fruitylink_serum",
}


def wheels(tree):
    """Every .whl under an installed FruityLink tree (the SDK wheel is installed more than once)."""
    return sorted(path for path in tree.rglob("*.whl") if path.is_file())


def compare(wheel, package, source):
    """Differences between one wheel's copy of `package` and the source directory."""
    problems = []
    with zipfile.ZipFile(wheel) as archive:
        packaged = {name for name in archive.namelist() if name.startswith(package + "/") and not name.endswith("/")}
        for name in sorted(packaged):
            path = source / Path(name).relative_to(package)
            if not path.is_file():
                problems.append(f"{name}: in the wheel but not in the source tree")
                continue
            if archive.read(name).replace(b"\r\n", b"\n") != path.read_bytes().replace(b"\r\n", b"\n"):
                problems.append(f"{name}: differs from {path}")
    for path in sorted(source.rglob("*")):
        if not path.is_file() or "__pycache__" in path.parts or path.suffix == ".pyc":
            continue
        name = f"{package}/{path.relative_to(source).as_posix()}"
        if name not in packaged:
            problems.append(f"{name}: missing from the wheel (rebuild it)")
    return problems


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    tree = Path(sys.argv[1]).expanduser().resolve()
    if not tree.is_dir():
        print(f"Not a directory: {tree}")
        return 2
    found = wheels(tree)
    if not found:
        print(f"No .whl found under {tree}; nothing is installed to check.")
        return 1
    failures = []
    checked = 0
    for wheel in found:
        with zipfile.ZipFile(wheel) as archive:
            names = archive.namelist()
        for package, source in PACKAGES.items():
            if not any(name.startswith(package + "/") for name in names):
                continue
            checked += 1
            for problem in compare(wheel, package, source):
                failures.append(f"{wheel.relative_to(tree)}: {problem}")
    if not checked:
        print(f"No wheel under {tree} carries {' or '.join(PACKAGES)}.")
        return 1
    if failures:
        print("\n".join(failures))
        print(f"\n{len(failures)} difference(s): the installed wheels are not built from this source tree.")
        return 1
    print(f"Checked {checked} installed package copy/copies in {len(found)} wheel(s) under {tree}: all match the source.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
