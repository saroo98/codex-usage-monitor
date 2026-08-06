#!/usr/bin/env python3
"""Fail when the publishable repository tree contains private or internal material."""
from __future__ import annotations

import os
from pathlib import Path
import re
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
MAX_TRACKED_BYTES = 5 * 1024 * 1024
TEXT_EXTENSIONS = {
    "", ".cmd", ".cs", ".csproj", ".css", ".html", ".js", ".json", ".md", ".props",
    ".appinstaller", ".manifest", ".ps1", ".py", ".slnx", ".svg", ".targets", ".txt",
    ".xaml", ".xml", ".yml", ".yaml",
}
ALLOWED_BINARY_EXTENSIONS = {".ico", ".png"}
PROHIBITED_BINARY_EXTENSIONS = {
    ".7z", ".bak", ".bundle", ".cer", ".db", ".dll", ".exe", ".key", ".log",
    ".msix", ".msixbundle", ".pfx", ".snk", ".sqlite", ".tar", ".zip",
}
PROHIBITED_PATH_PARTS = {".impeccable", "evidence"}
PROHIBITED_DOCUMENT_STEMS = {"design", "performance", "plan", "product", "roadmap"}
SECRET_PATTERNS = {
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "GitHub token": re.compile(r"\bgh[pousr]_[A-Za-z0-9_]{20,}\b"),
    "Google API key": re.compile(r"\bAIza[0-9A-Za-z_-]{20,}\b"),
    "AWS access key": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
}
EMAIL_PATTERN = re.compile(r"(?i)\b[A-Z0-9._%+-]+@([A-Z0-9.-]+\.[A-Z]{2,}|[A-Z0-9.-]+\.invalid)\b")
ALLOWED_EMAIL_DOMAINS = {
    "example.com", "example.invalid", "example.test", "openai.invalid", "users.noreply.github.com",
}
WINDOWS_USER_PATH = re.compile(r"(?i)[A-Z]:\\Users\\([^\\\s]+)")
ALLOWED_SYNTHETIC_USERS = {"<user>", "private-user", "runneradmin"}


def candidate_paths() -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    return sorted(
        (ROOT / raw.decode("utf-8", errors="strict") for raw in result.stdout.split(b"\0") if raw),
        key=lambda path: path.as_posix().casefold(),
    )


def audit() -> list[str]:
    errors: list[str] = []
    current_user = Path.home().name.casefold()
    for path in candidate_paths():
        if not path.is_file():
            continue
        relative = path.relative_to(ROOT)
        parts = {part.casefold() for part in relative.parts}
        suffix = path.suffix.casefold()
        stem = path.stem.casefold()

        if PROHIBITED_PATH_PARTS & parts:
            errors.append(f"internal-only path is tracked: {relative}")
        if suffix == ".md" and stem in PROHIBITED_DOCUMENT_STEMS:
            errors.append(f"internal planning document is tracked: {relative}")
        if suffix in PROHIBITED_BINARY_EXTENSIONS:
            errors.append(f"generated or sensitive binary is tracked: {relative}")
        if suffix not in TEXT_EXTENSIONS | ALLOWED_BINARY_EXTENSIONS:
            errors.append(f"unreviewed tracked file type {suffix or '<none>'}: {relative}")
        if path.stat().st_size > MAX_TRACKED_BYTES:
            errors.append(f"tracked file exceeds {MAX_TRACKED_BYTES} bytes: {relative}")
        if suffix not in TEXT_EXTENSIONS:
            continue

        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            errors.append(f"text file is not valid UTF-8: {relative}")
            continue
        if path.resolve() == Path(__file__).resolve():
            continue

        for label, pattern in SECRET_PATTERNS.items():
            if pattern.search(text):
                errors.append(f"possible {label} in {relative}")
        for match in EMAIL_PATTERN.finditer(text):
            domain = match.group(1).casefold()
            if domain not in ALLOWED_EMAIL_DOMAINS and not domain.endswith(
                (".example.com", ".example.invalid", ".example.test")
            ):
                errors.append(f"personal or unapproved email address in {relative}")
        for match in WINDOWS_USER_PATH.finditer(text):
            user = match.group(1).casefold()
            if user not in ALLOWED_SYNTHETIC_USERS and user == current_user:
                errors.append(f"local Windows user path in {relative}")

        repository_path = str(ROOT)
        if repository_path.casefold() in text.casefold():
            errors.append(f"local repository path in {relative}")
        home_path = str(Path.home())
        if home_path.casefold() in text.casefold():
            errors.append(f"local home path in {relative}")

    return sorted(set(errors))


def main() -> int:
    os.chdir(ROOT)
    errors = audit()
    if errors:
        print("Publication audit failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1
    print("Publication audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
