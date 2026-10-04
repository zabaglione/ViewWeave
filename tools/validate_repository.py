#!/usr/bin/env python3
"""Read-only static checks for the distributable repository surface."""
from __future__ import annotations
import argparse, json, os, re, sys
from pathlib import Path, PurePosixPath
from urllib.parse import urlparse

SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")
PACKAGE_NAME = re.compile(r"^com\.[a-z0-9]+(?:[.-][a-z0-9]+)*$")
FORBIDDEN = re.compile(r"(?:bridge_inproc\.dll|(?:^|[\\/_])bridge\.h$|lookingglass.*(?:sdk|official).*\.(?:dll|lib|h)|(?:^|[\\/_])(?:third.?party|vendor)[\\/_].*\.(?:dll|lib|h))", re.I)
LEGACY_ASMDEF_PREFIXES = ("Looking" + "Glass.", "Looking" + "GlassCommunity.")

class Reporter:
    def __init__(self): self.errors, self.warnings = [], []
    def _emit(self, prefix, message):
        safe = str(message).encode("ascii", "backslashreplace").decode("ascii")
        print(prefix + safe)
    def error(self, message): self.errors.append(str(message)); self._emit("ERROR: ", message)
    def warning(self, message): self.warnings.append(str(message)); self._emit("WARN: ", message)

def repo_files(root):
    ignored = {".git", ".local", "library", "temp", "build", "builds", "logs", "usersettings", "packagecache", "obj"}
    for current, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d.casefold() not in ignored and not (d.casefold().startswith("build") and Path(current).name.casefold() == "native")]
        for name in names: yield Path(current) / name

def valid_object(data, kind, path, report):
    if not isinstance(data, dict): report.error(f"{kind} must be a JSON object {path}"); return False
    return True

def package_check_profile(root, base, report, profile):
    packages, names = {}, set()
    for path in sorted((root / base).glob("*/package.json")):
        try: data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError): report.error(f"invalid {profile} package json {path.relative_to(root)}"); continue
        if not valid_object(data, "package", path.relative_to(root), report): continue
        name, version = data.get("name"), data.get("version")
        if not isinstance(name, str) or not PACKAGE_NAME.fullmatch(name): report.error(f"invalid package name {path.relative_to(root)}")
        if not isinstance(version, str) or not SEMVER.fullmatch(version): report.error(f"invalid package version {path.relative_to(root)}")
        if isinstance(name, str):
            if name in names: report.error(f"duplicate {profile} package name {name}")
            names.add(name); packages[name] = (path.parent, version)
    for name, (directory, _) in packages.items():
        try: data = json.loads((directory / "package.json").read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError): continue
        deps = data.get("dependencies", {})
        if not isinstance(deps, dict): report.error(f"dependencies must be an object {directory}"); continue
        for dep, constraint in deps.items():
            if not isinstance(dep, str) or not isinstance(constraint, str) or not PACKAGE_NAME.fullmatch(dep) or not SEMVER.fullmatch(constraint) or dep.startswith("http") or "://" in constraint:
                report.error(f"invalid dependency {name} -> {dep}"); continue
            if dep in packages and constraint != packages[dep][1]: report.error(f"local dependency version mismatch {name} -> {dep}")

def package_check(root, report):
    package_check_profile(root, "Packages", report, "source")
    if (root / "Distribution").is_dir():
        package_check_profile(root, "Distribution", report, "distribution")

def asmdef_check_profile(root, base, report, profile):
    names, entries = set(), []
    for path in sorted((root / base).glob("**/*.asmdef")):
        try: data = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError): report.error(f"invalid asmdef {path.relative_to(root)}"); continue
        if not valid_object(data, "asmdef", path.relative_to(root), report): continue
        name = data.get("name")
        if not isinstance(name, str) or not name: report.error(f"asmdef has no name {path.relative_to(root)}")
        elif name in names: report.error(f"duplicate {profile} asmdef name {name}")
        else: names.add(name)
        if isinstance(name, str) and name.startswith(LEGACY_ASMDEF_PREFIXES):
            report.error(f"legacy asmdef name {name} in {path.relative_to(root)}")
        entries.append((path, data))
    for path, data in entries:
        refs = data.get("references", [])
        if not isinstance(refs, list): report.error(f"asmdef references must be a list {path.relative_to(root)}"); continue
        for ref in refs:
            if not isinstance(ref, str): report.error(f"asmdef reference must be a string {path.relative_to(root)}"); continue
            if ref.startswith(LEGACY_ASMDEF_PREFIXES):
                report.error(f"legacy asmdef reference {ref} in {path.relative_to(root)}")
            elif ref.startswith("ViewWeave.") and ref not in names:
                report.error(f"unknown local asmdef reference {ref} in {path.relative_to(root)}")

def asmdef_check(root, report):
    asmdef_check_profile(root, "Packages", report, "source")
    distribution_root = root / "Distribution"
    if distribution_root.is_dir():
        for profile_root in sorted(path for path in distribution_root.iterdir() if path.is_dir()):
            asmdef_check_profile(root, str(profile_root.relative_to(root)), report, "distribution-" + profile_root.name)

def samples_check_profile(root, base, report, profile):
    found = False
    for package in sorted((root / base).glob("*/package.json")):
        try: data = json.loads(package.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError): report.error(f"invalid package json {package.relative_to(root)}"); continue
        if not isinstance(data, dict): report.error(f"package must be a JSON object {package.relative_to(root)}"); continue
        samples = data.get("samples", [])
        if not isinstance(samples, list): report.error(f"samples must be a list {package.relative_to(root)}"); continue
        for sample in samples:
            rel = sample.get("path") if isinstance(sample, dict) else None
            if isinstance(rel, str):
                normalized = rel.replace("\\", "/")
                parts = PurePosixPath(normalized).parts
                candidate = (package.parent / Path(*parts)).resolve()
                if not parts or parts[0] != "Samples~" or normalized.startswith("/") or ".." in parts or not candidate.is_relative_to(package.parent.resolve()): report.error(f"invalid sample path in {package.relative_to(root)}")
                elif not candidate.is_dir(): report.error(f"missing sample path {package.parent / rel}")
                else: found = True
            else: report.error(f"invalid sample entry in {package.relative_to(root)}")
    if not found: report.warning("no " + profile + " samples are registered")
    return found

def samples_check(root, report):
    source_found = samples_check_profile(root, "Packages", report, "source")
    distribution_found = samples_check_profile(root, "Distribution", report, "distribution") if (root / "Distribution").is_dir() else False
    return source_found or distribution_found

def meta_check_profile(root, base, report, profile):
    base_path = root / base
    if not base_path.is_dir():
        return
    for path in sorted(base_path.rglob("*")):
        if path.is_file() and path.suffix.lower() in {".cs", ".asmdef", ".dll"}:
            if not path.with_name(path.name + ".meta").is_file():
                report.error(f"missing meta for {profile} asset {path.relative_to(root)}")
    guids = {}
    for path in sorted(base_path.rglob("*.meta")):
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        match = re.search(r"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$", text)
        if not match:
            continue
        guid = match.group(1).lower()
        previous = guids.get(guid)
        if previous is not None:
            report.error(f"duplicate {profile} guid {guid}: {previous} and {path.relative_to(root)}")
        else:
            guids[guid] = path.relative_to(root).as_posix()

def links_check(root, report):
    links = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
    for path in repo_files(root):
        if path.suffix.lower() != ".md": continue
        try: text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError): continue
        for target in links.findall(text):
            target = target.strip().split(" ", 1)[0].strip("<>"); parsed = urlparse(target)
            if parsed.scheme or target.startswith("#") or "PLACEHOLDER" in target: continue
            if not (path.parent / parsed.path).resolve().exists(): report.warning(f"broken markdown link {path.relative_to(root)} -> {target}")

def surface_check(root, report):
    for path in repo_files(root):
        rel = path.relative_to(root).as_posix()
        if FORBIDDEN.search(rel): report.error(f"forbidden proprietary or third-party path {rel}")
        if path.name == ".env" or (path.name.startswith(".env.") and path.name != ".env.example") or path.suffix.lower() in {".key", ".pem"} or path.name.lower() in {"credentials.json", "credentials.yml", "credentials.yaml"}:
            report.error(f"possible credential path {rel}")

def main():
    parser = argparse.ArgumentParser(description="Validate repository files without modifying them")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1]); parser.add_argument("--release", action="store_true")
    args, report = parser.parse_args(), Reporter(); root = args.root.resolve()
    package_check(root, report); asmdef_check(root, report); samples_found = samples_check(root, report); links_check(root, report); surface_check(root, report)
    meta_check_profile(root, "Packages", report, "source")
    distribution_root = root / "Distribution"
    if distribution_root.is_dir():
        for profile_root in sorted(path for path in distribution_root.iterdir() if path.is_dir()):
            meta_check_profile(root, str(profile_root.relative_to(root)), report, "distribution-" + profile_root.name)
    if args.release:
        if not (root / "LICENSE").is_file() and not (root / "LICENSE.md").is_file(): report.error("release gate: license file is absent; legal clearance is manual")
        if not samples_found: report.error("release gate: distribution samples are absent")
        report.warning("release gate: public identity and device matrix require manual review"); report.warning("release gate: rights and Unity/render verification require manual review")
    print(f"RESULT: {'FAIL' if report.errors else 'PASS'} errors={len(report.errors)} warnings={len(report.warnings)}"); return 1 if report.errors else 0

if __name__ == "__main__": sys.exit(main())
