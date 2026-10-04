#!/usr/bin/env python3
"""Copy optional Bridge samples into the Unity test project.

The copied directories are ignored test inputs. This command never removes or
moves existing files, so it is safe to run from a clean clone before import.
"""

from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path


SAMPLES = ("WindowsUrpLit", "WindowsHdrpLit")


def ascii_text(value: object) -> str:
    return str(value).encode("ascii", "backslashreplace").decode("ascii")


def emit(message: object, error: bool = False) -> None:
    print(ascii_text(message), file=sys.stderr if error else sys.stdout)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prepare ignored Bridge sample copies for Unity tests."
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parent.parent,
        help="repository root (defaults to the directory containing tools)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="list copies without changing files",
    )
    return parser.parse_args()


def validate_root(root: Path) -> None:
    required = (root / "Packages", root / "Tests", root / "Packages" / "com.lookingglass.community.bridge")
    missing = [str(path) for path in required if not path.is_dir()]
    if missing:
        raise RuntimeError("repository root is invalid: " + ", ".join(ascii_text(path) for path in missing))


def copy_tree(source: Path, destination: Path, dry_run: bool) -> int:
    copied = 0
    for source_path in sorted(source.rglob("*")):
        relative = source_path.relative_to(source)
        destination_path = destination / relative
        if source_path.is_dir():
            if not dry_run:
                destination_path.mkdir(parents=True, exist_ok=True)
            continue
        if not source_path.is_file():
            continue
        if not dry_run:
            destination_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source_path, destination_path)
        emit("copy " + ascii_text(destination_path))
        copied += 1
    return copied


def main() -> int:
    args = parse_args()
    root = args.root.resolve()
    validate_root(root)
    source_root = root / "Packages" / "com.lookingglass.community.bridge" / "Samples~"
    destination_root = root / "Tests" / "Pipelines" / "Unity" / "Assets" / "Samples" / "ViewWeaveBridge"
    total = 0
    for sample in SAMPLES:
        source = source_root / sample
        destination = destination_root / sample
        if not source.is_dir():
            raise RuntimeError("sample source is missing: " + ascii_text(source))
        total += copy_tree(source, destination, args.dry_run)
    emit(("would copy " if args.dry_run else "copied ") + str(total) + " files")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError) as error:
        emit("error: " + ascii_text(error), error=True)
        raise SystemExit(1)
