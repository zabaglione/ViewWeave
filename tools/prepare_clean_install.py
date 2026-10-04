#!/usr/bin/env python3
"""Validate the two ignored clean-install Unity project inputs."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


PROJECTS = {
    "urp": "clean-install-urp",
    "hdrp": "clean-install-hdrp",
}


def ascii_text(value: object) -> str:
    return str(value).encode("ascii", "backslashreplace").decode("ascii")


def emit(value: object, error: bool = False) -> None:
    print(ascii_text(value), file=sys.stderr if error else sys.stdout)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate clean Unity install inputs.")
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parent.parent,
        help="repository root",
    )
    return parser.parse_args()


def check_project(root: Path, name: str, directory: str) -> int:
    project = root / ".local" / directory
    required = (
        project / "Packages" / "manifest.json",
        project / "ProjectSettings" / "ProjectVersion.txt",
        project / "Assets" / "Editor" / "CleanInstallSmokeTests.cs",
        project / "Assets" / "Editor" / "ViewWeave.CleanInstall.asmdef",
    )
    missing = [path for path in required if not path.is_file()]
    if missing:
        raise RuntimeError(name + " missing: " + ", ".join(ascii_text(path) for path in missing))
    manifest = json.loads((project / "Packages" / "manifest.json").read_text(encoding="utf-8"))
    dependencies = manifest.get("dependencies", {})
    for package in ("com.zabaglione.viewweave.core", "com.zabaglione.viewweave.bridge"):
        value = dependencies.get(package, "")
        if not value.startswith("file:"):
            raise RuntimeError(name + " requires a relative file dependency for " + package)
        package_path = (project / "Packages" / value[5:]).resolve()
        if not package_path.is_dir():
            raise RuntimeError(name + " dependency is missing: " + ascii_text(package_path))
    if name == "urp" and "com.zabaglione.viewweave.urp" not in dependencies:
        raise RuntimeError("urp adapter dependency is missing")
    if name == "hdrp" and "com.zabaglione.viewweave.hdrp" not in dependencies:
        raise RuntimeError("hdrp adapter dependency is missing")
    emit("ok " + name)
    return 1


def main() -> int:
    args = parse_args()
    root = args.root.resolve()
    if not (root / "Packages").is_dir() or not (root / "Tests").is_dir():
        raise RuntimeError("repository root is invalid: " + ascii_text(root))
    checked = 0
    for name, directory in PROJECTS.items():
        checked += check_project(root, name, directory)
    emit("checked " + str(checked) + " clean install projects")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, json.JSONDecodeError) as error:
        emit("error: " + ascii_text(error), error=True)
        raise SystemExit(1)
