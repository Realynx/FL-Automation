"""Repeatable native Python quality gate; no rule suppressions or source exclusions."""

import subprocess
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def main() -> None:
    print(f"Running Python quality gate on Python {sys.version.split()[0]}", flush=True)
    project = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    sdk_version = (ROOT.parent / "VERSION").read_text(encoding="utf-8").strip()
    if project["project"]["version"] != sdk_version:
        raise SystemExit("Python package version must match sdk/VERSION.")
    commands = [
        [sys.executable, "tools/generate_operations.py", "--check"],
        [sys.executable, "-m", "ruff", "check", "."],
        [sys.executable, "-m", "mypy"],
        [sys.executable, "-m", "pytest", "-q"],
        [sys.executable, "-m", "build"],
    ]
    for command in commands:
        subprocess.run(command, cwd=ROOT, check=True)


if __name__ == "__main__":
    main()
