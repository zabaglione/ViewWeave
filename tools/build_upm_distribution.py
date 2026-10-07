#!/usr/bin/env python3
"""Build and verify the self-contained Git-installable UPM distributions."""
from __future__ import annotations

import argparse
import filecmp
import hashlib
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DIST = ROOT / "Distribution"
VERSION = "0.1.0"
UNITY = "6000.6"
RP_VERSION = "17.6.0"


def log(message: str) -> None:
    safe = str(message).encode("ascii", "backslashreplace").decode("ascii")
    print("[upm-distribution] " + safe)


def source_package(part: str) -> Path:
    expected_name = "com.zabaglione.lookingglass.viewweave." + part
    matches = []
    for package_json in sorted((ROOT / "Packages").glob("*/package.json")):
        try:
            data = json.loads(package_json.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            continue
        if data.get("name") == expected_name:
            matches.append(package_json.parent)
    if len(matches) != 1:
        raise RuntimeError("expected source package %s, found %d" % (expected_name, len(matches)))
    return matches[0]


def copy_tree(source: Path, target: Path) -> None:
    if not source.is_dir():
        raise RuntimeError("missing source directory: %s" % source)
    for item in sorted(source.rglob("*")):
        relative = item.relative_to(source)
        destination = target / relative
        if item.is_dir():
            destination.mkdir(parents=True, exist_ok=True)
        elif item.is_file():
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, destination)


def copy_component(source: Path, target: Path, relative: str, destination_relative: str) -> None:
    source_path = source / relative
    if source_path.exists():
        destination = target / destination_relative
        copy_tree(source_path, destination)
        source_meta = source / (relative + ".meta")
        if source_meta.is_file():
            shutil.copy2(source_meta, destination.parent / (destination.name + ".meta"))
    else:
        raise RuntimeError("missing package component: %s" % source_path)


def write_folder_meta(path: Path, seed: str) -> None:
    digest = hashlib.sha1(seed.encode("ascii")).hexdigest()[:32]
    write_text(
        path,
        "fileFormatVersion: 2\n"
        "guid: " + digest + "\n"
        "folderAsset: yes\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData:\n"
        "  assetBundleName:\n"
        "  assetBundleVariant:\n",
    )


def write_file_meta(path: Path, seed: str) -> None:
    digest = hashlib.sha1(seed.encode("ascii")).hexdigest()[:32]
    write_text(
        path,
        "fileFormatVersion: 2\n"
        "guid: " + digest + "\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData:\n"
        "  assetBundleName:\n"
        "  assetBundleVariant:\n",
    )


def package_json(kind: str) -> dict:
    rp = "universal" if kind == "urp" else "high-definition"
    package_suffix = kind
    sample_name = "WindowsUrpLit" if kind == "urp" else "WindowsHdrpLit"
    sample_label = "URP" if kind == "urp" else "HDRP"
    samples = [
        {
            "displayName": "Windows " + sample_label + " Lit",
            "description": "Optional Windows Bridge demo scene for " + sample_label + "; the Bridge Worker supports D3D11 and D3D12, and standard Cube and Box scenes need no sample import.",
            "path": "Samples~/" + sample_name,
        }
    ]
    return {
        "name": "com.zabaglione.lookingglass.viewweave." + package_suffix,
        "version": VERSION,
        "displayName": "ViewWeave for Looking Glass - " + kind.upper() + " (Unofficial)",
        "description": "Self-contained unofficial ViewWeave " + kind.upper() + " package for Unity 6.",
        "license": "Apache-2.0",
        "unity": UNITY,
        "author": {"name": "zabaglione"},
        "dependencies": {
            "com.unity.render-pipelines." + rp: RP_VERSION,
            "com.unity.modules.imageconversion": "1.0.0",
            "com.unity.modules.jsonserialize": "1.0.0",
        },
        "keywords": ["looking-glass", "quilt", kind],
        "samples": samples,
    }


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text.rstrip() + "\n", encoding="utf-8", newline="\n")


def build(kind: str, target: Path) -> None:
    core = source_package("core")
    bridge = source_package("bridge")
    rp = source_package(kind)
    for source in (core, bridge, rp):
        source_data = json.loads((source / "package.json").read_text(encoding="utf-8"))
        if source_data.get("version") != VERSION:
            raise RuntimeError("source version mismatch in %s" % source)
    target.mkdir(parents=True, exist_ok=True)
    viewweave_root = target / "ViewWeave"
    viewweave_root.mkdir(parents=True, exist_ok=True)
    write_folder_meta(target / "ViewWeave.meta", kind + ":ViewWeave")

    for package, components in (
        (core, ("Runtime", "Editor")),
        (bridge, ("Runtime", "Editor", "Plugins")),
        (rp, ("Runtime", "Editor")),
    ):
        component_root = "ViewWeave/" + ("Core" if package == core else "Bridge" if package == bridge else kind.upper())
        root_path = target / component_root
        root_path.mkdir(parents=True, exist_ok=True)
        write_folder_meta(root_path.with_name(root_path.name + ".meta"), kind + ":" + component_root)
        for component in components:
            copy_component(package, target, component, component_root + "/" + component)

    sample = "Samples~/Windows" + ("UrpLit" if kind == "urp" else "HdrpLit")
    copy_component(bridge, target, sample, sample)

    # Keep the repository license and a plain attribution record at the package root.
    shutil.copy2(ROOT / "LICENSE", target / "LICENSE")
    write_text(
        target / "NOTICE.md",
        "ViewWeave distribution notice\n\n"
        "This package is generated from the ViewWeave repository. The bundled native bridge "
        "artifact and its source hashes are documented in ViewWeave/Bridge/Plugins/native-provenance.json.\n",
    )
    shutil.copy2(ROOT / "docs/THIRD_PARTY_NOTICES.md", target / "THIRD_PARTY_NOTICES.md")
    write_text(
        target / "README.md",
        "# ViewWeave " + kind.upper() + " Git package\n\n"
        "Install this folder from the repository using Unity Package Manager:\n\n"
        "    https://github.com/zabaglione/ViewWeave.git?path=/Distribution/" + kind + "#main\n\n"
        "Use Unity 6000.6.4f1 with the matching Unity " + kind.upper() + " render pipeline. "
        "Start with an ordinary Cube or Box, right-click the Main Camera in the Hierarchy, "
        "and choose ViewWeave > Bridge Rig to create a child rig; "
        "no sample import or object script is required. Bridge Worker output supports Windows "
        "D3D11 and D3D12 Editor Play and Player. D3D12 shares GPU resources with a D3D11 "
        "worker on the same adapter and does not read image pixels back to the CPU. Install "
        "and run the Looking Glass Bridge runtime separately. Legacy Direct output is D3D11-only. "
        "The optional Windows sample is imported only through Package Manager Samples.\n",
    )
    package_path = target / "package.json"
    package_path.write_text(json.dumps(package_json(kind), indent=2, ensure_ascii=True) + "\n", encoding="utf-8", newline="\n")
    for file_name in ("package.json", "README.md", "LICENSE", "NOTICE.md", "THIRD_PARTY_NOTICES.md"):
        write_file_meta(target / (file_name + ".meta"), kind + ":root:" + file_name)


def files(path: Path) -> set[Path]:
    return {item.relative_to(path) for item in path.rglob("*") if item.is_file()}


def compare(expected: Path, actual: Path) -> list[str]:
    errors = []
    expected_files = files(expected)
    actual_files = files(actual)
    for relative in sorted(expected_files - actual_files):
        errors.append("missing " + relative.as_posix())
    for relative in sorted(actual_files - expected_files):
        errors.append("extra " + relative.as_posix())
    for relative in sorted(expected_files & actual_files):
        if not filecmp.cmp(expected / relative, actual / relative, shallow=False):
            errors.append("different " + relative.as_posix())
    return errors


def validate_target(target: Path) -> None:
    data = json.loads((target / "package.json").read_text(encoding="utf-8"))
    expected_name = "com.zabaglione.lookingglass.viewweave." + target.name
    if data.get("name") != expected_name:
        raise RuntimeError("wrong package name in %s" % target)
    dependency = "universal" if target.name == "urp" else "high-definition"
    if data.get("dependencies", {}).get("com.unity.render-pipelines." + dependency) != RP_VERSION:
        raise RuntimeError("wrong render pipeline dependency in %s" % target)


def target_path(kind: str) -> Path:
    target = (DIST / kind).resolve()
    if target.parent != DIST.resolve() or kind not in {"urp", "hdrp"}:
        raise RuntimeError("unsafe distribution target")
    return target


def run(kind: str, check: bool) -> int:
    target = target_path(kind)
    with tempfile.TemporaryDirectory(prefix="viewweave-upm-") as temporary:
        staged = Path(temporary) / kind
        build(kind, staged)
        validate_target(staged)
        if check:
            errors = compare(staged, target) if target.is_dir() else ["missing target " + target.as_posix()]
            if errors:
                for error in errors:
                    log(error)
                return 1
            log("check passed " + kind)
            return 0
        if target.exists():
            if target.is_symlink() or not target.is_dir():
                raise RuntimeError("distribution target is not a directory: %s" % target)
            shutil.rmtree(target)
        shutil.move(str(staged), str(target))
        log("generated " + target.as_posix())
        return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("kind", choices=("urp", "hdrp", "all"), nargs="?", default="all")
    parser.add_argument("--check", action="store_true", help="check generated files without changing them")
    args = parser.parse_args()
    try:
        kinds = ("urp", "hdrp") if args.kind == "all" else (args.kind,)
        for kind in kinds:
            result = run(kind, args.check)
            if result:
                return result
        return 0
    except (OSError, RuntimeError, json.JSONDecodeError) as error:
        log("error: " + str(error))
        return 1


if __name__ == "__main__":
    sys.exit(main())
