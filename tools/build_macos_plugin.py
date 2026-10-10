#!/usr/bin/env python3
"""Build and package the ViewWeave macOS Bridge Metal shim."""

from __future__ import annotations

import argparse
import hashlib
import os
import json
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / "Native"
BRIDGE_PACKAGE = ROOT / "Packages" / "com.zabaglione.lookingglass.viewweave.bridge"
PINNED_COMMIT = "522254181faf188efa8b50c3e3bf6fce720b26e4"
REQUIRED_HEADERS = ("IUnityInterface.h", "IUnityGraphics.h", "IUnityGraphicsMetal.h")
DEFAULT_BUILD_DIR = NATIVE / "build-macos-validation"
PLUGIN_NAME = "lookingglass_bridge_shim.dylib"
PLUGIN_GUID = "9ae39fc365aa4d3e98238dc4762aec26"
PROVENANCE_GUID = "5f3c9c61c7fb4b56a42f59e2f8e5b2ac"
MACOS_FOLDER_GUID = "ca84de192fc94c27b7033fabd425b634"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ascii_text(value: str) -> str:
    return value.encode("ascii", "replace").decode("ascii")


def run_command(command: list[str], *, cwd: Path, label: str) -> dict[str, Any]:
    print(ascii_text("[build_macos_plugin] " + label + ": " + " ".join(command)), flush=True)
    completed = subprocess.run(command, cwd=str(cwd), text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    output = completed.stdout or ""
    if output:
        printable_output = ascii_text(output)
        print(printable_output, end="" if printable_output.endswith("\n") else "\n", flush=True)
    result = {
        "command": command,
        "returnCode": completed.returncode,
        "outputTail": output.splitlines()[-24:],
    }
    if completed.returncode != 0:
        raise RuntimeError(label + " failed with exit code " + str(completed.returncode))
    return result


def git_revision(path: Path) -> str | None:
    completed = subprocess.run(
        ["git", "-C", str(path), "rev-parse", "HEAD"],
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
    )
    return completed.stdout.strip() if completed.returncode == 0 else None


def prepare_headers(build_dir: Path, supplied_headers: Path | None) -> tuple[Path, str, str | None]:
    if supplied_headers is not None:
        header_dir = supplied_headers.expanduser().resolve()
        source = "provided"
        revision = git_revision(header_dir)
        if revision != PINNED_COMMIT:
            raise RuntimeError("provided Unity PluginAPI headers are from " + (revision or "an unverified source") + ", expected " + PINNED_COMMIT)
    else:
        header_dir = build_dir / "UnityPluginAPI"
        header_dir.mkdir(parents=True, exist_ok=True)
        base = "https://raw.githubusercontent.com/Unity-Technologies/NativeRenderingPlugin/" + PINNED_COMMIT + "/PluginSource/source/Unity/"
        for header in REQUIRED_HEADERS:
            destination = header_dir / header
            request = urllib.request.Request(base + header, headers={"User-Agent": "ViewWeave-macos-plugin-builder"})
            with urllib.request.urlopen(request, timeout=30) as response:
                content = response.read()
            if not content:
                raise RuntimeError("official Unity header download was empty: " + header)
            destination.write_bytes(content)
        source = base
        revision = PINNED_COMMIT

    missing = [name for name in REQUIRED_HEADERS if not (header_dir / name).is_file()]
    if missing:
        raise RuntimeError("Unity PluginAPI directory is missing required headers: " + ", ".join(missing))
    return header_dir, source, revision


def native_source_hashes() -> dict[str, str]:
    suffixes = {".c", ".cc", ".cpp", ".h", ".hh", ".hpp", ".inc", ".m", ".mm", ".cmake"}
    hashes: dict[str, str] = {}
    for path in sorted(NATIVE.rglob("*")):
        if not path.is_file() or path.suffix not in suffixes and path.name != "CMakeLists.txt":
            continue
        relative = path.relative_to(NATIVE)
        if any(part == "build" or part.startswith("build-") or part in {"CMakeFiles", ".git"} for part in relative.parts):
            continue
        hashes["Native/" + relative.as_posix()] = sha256_file(path)
    return hashes


def cmake_compiler_identity(build_dir: Path) -> dict[str, str]:
    compiler_files = sorted(build_dir.glob("CMakeFiles/*/CMakeCXXCompiler.cmake"))
    if not compiler_files:
        raise RuntimeError("CMake C++ compiler metadata was not generated")
    compiler_info = compiler_files[-1].read_text(encoding="utf-8", errors="replace")
    compiler_id = re.search(r'^set\(CMAKE_CXX_COMPILER_ID "([^"]+)"\)', compiler_info, re.MULTILINE)
    compiler_version = re.search(r'^set\(CMAKE_CXX_COMPILER_VERSION "([^"]+)"\)', compiler_info, re.MULTILINE)
    cache_path = build_dir / "CMakeCache.txt"
    cache_text = cache_path.read_text(encoding="utf-8", errors="replace") if cache_path.is_file() else ""
    compiler_path = re.search(r"^CMAKE_CXX_COMPILER:[^=]+=([^\r\n]+)", cache_text, re.MULTILINE)
    if compiler_id is None or compiler_version is None:
        raise RuntimeError("CMake compiler ID or version is missing from generated metadata")
    identity = {"id": compiler_id.group(1), "version": compiler_version.group(1)}
    if compiler_path is not None:
        identity["path"] = compiler_path.group(1)
    return identity


def portable_value(value: Any, replacements: list[tuple[str, str]]) -> Any:
    if isinstance(value, str):
        result = value
        for source, replacement in replacements:
            if source:
                result = result.replace(source, replacement)
        return result
    if isinstance(value, list):
        return [portable_value(item, replacements) for item in value]
    if isinstance(value, dict):
        return {key: portable_value(item, replacements) for key, item in value.items()}
    return value


def plugin_importer_meta(cpu: str) -> str:
    return """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 3
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 1
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
    Editor:
      enabled: 1
      settings:
        CPU: {cpu}
        DefaultValueInitialized: true
        OS: OSX
    Linux64:
      enabled: 0
      settings:
        CPU: x86_64
    OSXUniversal:
      enabled: 1
      settings:
        CPU: {cpu}
    Win:
      enabled: 0
      settings:
        CPU: None
    Win64:
      enabled: 0
      settings:
        CPU: x86_64
  userData:
  assetBundleName:
  assetBundleVariant:
""".format(guid=PLUGIN_GUID, cpu=cpu)


def find_built_plugin(build_dir: Path) -> Path:
    built_names = {PLUGIN_NAME, "lib" + PLUGIN_NAME}
    candidates = [build_dir / name for name in built_names]
    candidates.extend(build_dir / "Release" / name for name in built_names)
    matches = {candidate for candidate in candidates if candidate.is_file()}
    matches.update(path for path in build_dir.rglob("*")
                   if path.is_file() and path.name in built_names and "CMakeFiles" not in path.parts)
    matches = sorted(matches)
    if len(matches) == 1:
        return matches[0]
    if matches:
        raise RuntimeError("multiple built shim libraries found; expected one in the selected build directory")
    raise RuntimeError("CMake did not produce " + PLUGIN_NAME)


def install_plugin(built_plugin: Path, build_dir: Path, architectures: list[str], compiler: dict[str, str],
                   build_commands: list[dict[str, Any]],
                   test_command: dict[str, Any], header_dir: Path, header_source: str,
                   header_revision: str | None, lipo_output: str) -> Path:
    macos_folder = BRIDGE_PACKAGE / "Plugins" / "macOS"
    macos_folder.mkdir(parents=True, exist_ok=True)
    (macos_folder.parent / "macOS.meta").write_text(
        "fileFormatVersion: 2\nguid: " + MACOS_FOLDER_GUID + "\nfolderAsset: yes\nDefaultImporter:\n"
        "  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n",
        encoding="utf-8",
    )
    destination = macos_folder / PLUGIN_NAME
    # Reusing a loaded Mach-O inode can leave macOS code-signing pages stale.
    # Install with a new inode; existing Editors retain their old mapped library until restart.
    with tempfile.NamedTemporaryFile(dir=macos_folder, prefix=PLUGIN_NAME + ".", suffix=".tmp", delete=False) as stream:
        staged_plugin = Path(stream.name)
    try:
        shutil.copy2(built_plugin, staged_plugin)
        os.replace(staged_plugin, destination)
    finally:
        staged_plugin.unlink(missing_ok=True)
    importer_cpu = "AnyCPU" if len(architectures) > 1 else "ARM64"
    (macos_folder / (PLUGIN_NAME + ".meta")).write_text(plugin_importer_meta(importer_cpu), encoding="utf-8")

    test_output = "\n".join(test_command.get("outputTail", []))
    summary_match = re.search(r"\d+% tests passed(?:, \d+ tests failed out of \d+| out of \d+)", test_output)
    header_hashes = {name: sha256_file(header_dir / name) for name in REQUIRED_HEADERS}
    provenance = {
        "formatVersion": 1,
        "artifact": PLUGIN_NAME,
        "artifactSha256": sha256_file(destination),
        "originalCodeLicense": "Apache-2.0",
        "thirdPartyNotice": "../../NOTICE.md",
        "configuration": "Release",
        "buildDirectory": Path(os.path.relpath(build_dir, ROOT)).as_posix(),
        "compiler": compiler,
        "architectures": architectures,
        "unityNativeRenderingPlugin": {
            "repository": "https://github.com/Unity-Technologies/NativeRenderingPlugin",
            "commit": header_revision,
            "source": header_source,
            "providedPath": "${UNITY_PLUGIN_API_DIR}" if header_source == "provided" else None,
            "headerSha256": header_hashes,
            "headerLicenseNotices": {
                "IUnityInterface.h": "Unity Companion License",
                "IUnityGraphics.h": "Unity Companion License",
                "IUnityGraphicsMetal.h": "Repository-level MIT notice; includes UCL IUnityInterface.h",
            },
            "licenseFiles": [
                "../../ThirdPartyLicenses/Unity-Companion-License.txt",
                "../../ThirdPartyLicenses/Unity-NativeRenderingPlugin-MIT.txt",
            ],
        },
        "nativeSourceSha256": native_source_hashes(),
        "buildCommands": build_commands,
        "ctest": {
            "command": test_command["command"],
            "returnCode": test_command["returnCode"],
            "summary": summary_match.group(0) if summary_match else "See outputTail",
            "outputTail": test_command["outputTail"],
        },
        "lipoArchitectures": lipo_output,
        "platform": platform.platform(),
        "python": platform.python_version(),
    }
    home = Path.home().resolve().as_posix()
    replacements = [
        (header_dir.resolve().as_posix(), "${UNITY_PLUGIN_API_DIR}"),
        (ROOT.resolve().as_posix(), "${REPOSITORY_ROOT}"),
        (home, "${USER_HOME}"),
    ]
    provenance = portable_value(provenance, replacements)
    provenance_path = macos_folder / "native-provenance.json"
    provenance_path.write_text(json.dumps(provenance, indent=2, sort_keys=True, ensure_ascii=True) + "\n", encoding="utf-8")
    provenance_path.with_suffix(provenance_path.suffix + ".meta").write_text(
        "fileFormatVersion: 2\nguid: " + PROVENANCE_GUID + "\nTextScriptImporter:\n"
        "  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n",
        encoding="utf-8",
    )
    return destination


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build and package the macOS ViewWeave Bridge Metal shim.")
    parser.add_argument("--headers", type=Path, help="reuse a Unity PluginAPI header directory")
    parser.add_argument("--universal", action="store_true", help="build both arm64 and x86_64 slices")
    parser.add_argument("--build-dir", type=Path, default=DEFAULT_BUILD_DIR, help="ignored CMake build/cache directory")
    parser.add_argument("--no-install", action="store_true", help="build and test without copying files into the package")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if sys.platform != "darwin":
        print("[build_macos_plugin] Error: this build requires macOS and Apple Metal SDKs.", file=sys.stderr)
        return 2
    if shutil.which("cmake") is None:
        print("[build_macos_plugin] Error: cmake is not available on PATH.", file=sys.stderr)
        return 2

    try:
        build_dir = args.build_dir.expanduser().resolve()
        build_dir.mkdir(parents=True, exist_ok=True)
        header_dir, header_source, header_revision = prepare_headers(build_dir, args.headers)
    except (OSError, RuntimeError, subprocess.SubprocessError, urllib.error.URLError) as error:
        print(ascii_text("[build_macos_plugin] Error: " + str(error)), file=sys.stderr, flush=True)
        return 1
    architectures = ["arm64", "x86_64"] if args.universal else ["arm64"]
    architecture_setting = ";".join(architectures)
    configure = [
        "cmake", "-S", str(NATIVE), "-B", str(build_dir),
        "-DUNITY_PLUGIN_API_DIR=" + str(header_dir),
        "-DBUILD_TESTING=ON",
        "-DCMAKE_BUILD_TYPE=Release",
        "-DCMAKE_OSX_ARCHITECTURES=" + architecture_setting,
    ]
    try:
        configure_result = run_command(configure, cwd=ROOT, label="CMake configure")
        compiler = cmake_compiler_identity(build_dir)
        build_result = run_command(["cmake", "--build", str(build_dir), "--config", "Release", "--parallel"], cwd=ROOT, label="CMake Release build")
        ctest_result = run_command(["ctest", "--test-dir", str(build_dir), "-C", "Release", "--output-on-failure"], cwd=ROOT, label="CTest")
        built_plugin = find_built_plugin(build_dir)
        lipo_result = run_command(["lipo", "-archs", str(built_plugin)], cwd=ROOT, label="Mach-O architecture check")
        actual_architectures = set(lipo_result["outputTail"][-1].split()) if lipo_result["outputTail"] else set()
        if actual_architectures != set(architectures):
            raise RuntimeError("built dylib architectures do not match request: " + ", ".join(sorted(actual_architectures)))
        build_commands = [configure_result, build_result, lipo_result]
        if args.no_install:
            print("[build_macos_plugin] Build and tests passed; plugin installation was skipped.", flush=True)
            return 0
        destination = install_plugin(
            built_plugin, build_dir, architectures, compiler, build_commands, ctest_result, header_dir,
            header_source, header_revision, lipo_result["outputTail"][-1],
        )
        print("[build_macos_plugin] Installed plugin: " + str(destination), flush=True)
        print("[build_macos_plugin] Provenance: " + str(destination.parent / "native-provenance.json"), flush=True)
        return 0
    except (OSError, RuntimeError, subprocess.SubprocessError, urllib.error.URLError) as error:
        print(ascii_text("[build_macos_plugin] Error: " + str(error)), file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
