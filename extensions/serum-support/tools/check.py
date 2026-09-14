"""Run the optional Serum extension's complete local quality gate."""

from __future__ import annotations

import os
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path

SDK_ROOT = Path(__file__).resolve().parents[3]
EXTENSION_ROOT = SDK_ROOT / "extensions" / "serum-support"
SDK_SOURCE = SDK_ROOT / "python" / "src"
EXTENSION_SOURCE = EXTENSION_ROOT / "src"
CONFIG = EXTENSION_ROOT / "pyproject.toml"


def run(*arguments: str, environment: dict[str, str] | None = None) -> None:
    command = [sys.executable, "-m", *arguments]
    print("+", subprocess.list2cmdline(command), flush=True)
    subprocess.run(command, cwd=SDK_ROOT, env=environment, check=True)


def source_environment() -> dict[str, str]:
    environment = os.environ.copy()
    local_sources = os.pathsep.join((str(EXTENSION_SOURCE), str(SDK_SOURCE)))
    existing = environment.get("PYTHONPATH")
    environment["PYTHONPATH"] = local_sources if not existing else local_sources + os.pathsep + existing
    environment["MYPYPATH"] = local_sources
    return environment


def check_archives(output: Path) -> None:
    wheel = next(output.glob("*.whl"))
    sdist = next(output.glob("*.tar.gz"))
    with zipfile.ZipFile(wheel) as archive:
        wheel_names = archive.namelist()
    allowed_wheel = ("fruitylink_serum/", "fruitylink_serum-0.1.0.dist-info/")
    unexpected = [name for name in wheel_names if not name.startswith(allowed_wheel)]
    if unexpected:
        raise RuntimeError(f"Wheel contains unexpected entries: {unexpected}")
    required_wheel = ("fruitylink_serum/__init__.py", ".dist-info/METADATA", ".dist-info/licenses/LICENSE")
    for required in required_wheel:
        if not any(name.endswith(required) for name in wheel_names):
            raise RuntimeError(f"Wheel is missing required entry: {required}")

    with tarfile.open(sdist, "r:gz") as archive:
        sdist_names = archive.getnames()
    forbidden = {"artifacts", "dist", ".venv", "__pycache__"}
    polluted = [name for name in sdist_names if forbidden.intersection(Path(name).parts)]
    if polluted:
        raise RuntimeError(f"Source distribution contains build artifacts: {polluted}")
    nested_archives = [name for name in sdist_names if name.endswith((".whl", ".tar.gz"))]
    if nested_archives:
        raise RuntimeError(f"Source distribution contains nested archives: {nested_archives}")


def main() -> None:
    environment = source_environment()
    run("ruff", "check", "--config", str(CONFIG),
        "--config", "lint.isort.known-first-party=['fruitylink_serum']", str(EXTENSION_ROOT))
    run("mypy", "--config-file", str(CONFIG), str(EXTENSION_SOURCE),
        str(EXTENSION_ROOT / "tests"), environment=environment)
    run("pytest", "-c", str(CONFIG), str(EXTENSION_ROOT / "tests"), environment=environment)
    with tempfile.TemporaryDirectory(prefix="fruitylink-serum-build-") as output:
        run("build", str(EXTENSION_ROOT), "--outdir", output, environment=environment)
        check_archives(Path(output))


if __name__ == "__main__":
    main()
