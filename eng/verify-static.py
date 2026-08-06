#!/usr/bin/env python3
"""Cross-platform repository structure and text validation.

This does not replace a Windows .NET build. It gives contributors without the
Windows toolchain a deterministic preflight for solution paths, XML/XAML,
workflow YAML, unsafe placeholders, and accidental private planning material.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

try:
    import yaml
except ImportError:  # pragma: no cover
    yaml = None

ROOT = Path(__file__).resolve().parents[1]
ERRORS: list[str] = []
EXCLUDED_DIRECTORIES = {".git", ".vs", ".idea", "artifacts", "bin", "obj", "packages"}


def error(message: str) -> None:
    ERRORS.append(message)


def repository_paths(pattern: str = "*") -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    paths = [ROOT / item.decode("utf-8") for item in result.stdout.split(b"\0") if item]
    return [
        path
        for path in paths
        if path.match(pattern)
        and not EXCLUDED_DIRECTORIES.intersection(part.lower() for part in path.relative_to(ROOT).parts)
    ]


def verify_solution() -> None:
    text = (ROOT / "CodexUsageMonitor.slnx").read_text(encoding="utf-8")
    for relative in re.findall(r'Project Path="([^"]+)"', text):
        if not (ROOT / relative).is_file():
            error(f"solution references missing project: {relative}")


def verify_xml() -> None:
    for path in sorted(repository_paths("*.xaml")) + sorted(repository_paths("*.csproj")) + [ROOT / "CodexUsageMonitor.slnx"]:
        try:
            ET.parse(path)
        except (OSError, ET.ParseError) as exc:
            error(f"invalid XML {path.relative_to(ROOT)}: {exc}")


def verify_json() -> None:
    for path in [ROOT / "global.json"]:
        try:
            json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as exc:
            error(f"invalid JSON {path.relative_to(ROOT)}: {exc}")


def verify_yaml() -> None:
    if yaml is None:
        return
    for path in sorted((ROOT / ".github" / "workflows").glob("*.yml")) if (ROOT / ".github" / "workflows").exists() else []:
        try:
            yaml.safe_load(path.read_text(encoding="utf-8"))
        except Exception as exc:  # PyYAML exposes multiple parser exception types.
            error(f"invalid YAML {path.relative_to(ROOT)}: {exc}")


def verify_public_tree() -> None:
    prohibited_names = {
        "Codex_Usage_Monitor_Windows_1.0_Master_Plan.md",
        "Codex_Usage_Monitor_1.0_Final_Implementation_Specification.md",
    }
    for path in repository_paths():
        if path.name in prohibited_names or "private-plan" in path.name.lower():
            error(f"private planning material is present: {path.relative_to(ROOT)}")

    placeholders = re.compile(r"\b(REPLACE_WITH_|TBD_SECRET|BEGIN PRIVATE KEY|BEGIN CERTIFICATE)\b")
    for path in repository_paths():
        if not path.is_file() or path.suffix.lower() in {".png", ".ico", ".zip", ".dll", ".exe", ".pdb"}:
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue
        if path == Path(__file__).resolve():
            continue
        if placeholders.search(text) and "templates" not in path.parts:
            error(f"release placeholder found outside a template: {path.relative_to(ROOT)}")


def main() -> int:
    verify_solution()
    verify_xml()
    verify_json()
    verify_yaml()
    verify_public_tree()
    publication_audit = subprocess.run([sys.executable, ROOT / "eng" / "audit-publication.py"], cwd=ROOT)
    if publication_audit.returncode != 0:
        error("publication audit failed")
    website_verification = subprocess.run([sys.executable, ROOT / "eng" / "verify-site.py"], cwd=ROOT)
    if website_verification.returncode != 0:
        error("website verification failed")
    if ERRORS:
        print("Static verification failed:", file=sys.stderr)
        for item in ERRORS:
            print(f"- {item}", file=sys.stderr)
        return 1
    print("Static repository verification passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
