#!/usr/bin/env python3
"""Generate and verify the updater's deterministic per-file integrity manifest."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
from typing import Final

SCHEMA_VERSION: Final = 1
MANIFEST_NAME: Final = "update-files.json"
MAX_FILE_COUNT: Final = 4096
MAX_FILE_BYTES: Final = 512 * 1024 * 1024
MAX_PACKAGE_BYTES: Final = 1024 * 1024 * 1024
MAX_SERIALIZED_BYTES: Final = 512 * 1024
MAX_RELATIVE_PATH_CHARACTERS: Final = 512
MAX_SEGMENT_CHARACTERS: Final = 160
RESERVED_NAMES: Final = {"CON", "PRN", "AUX", "NUL"}
SEMVER_PATTERN: Final = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-((?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)


class ManifestError(ValueError):
    """Raised when a publish tree cannot be represented safely."""


def _validate_version(version: str) -> None:
    if not SEMVER_PATTERN.fullmatch(version):
        raise ManifestError(f"version is not canonical semantic version text: {version!r}")


def _is_reserved_device(segment: str) -> bool:
    base = segment.split(".", 1)[0].upper()
    if base in RESERVED_NAMES:
        return True
    return len(base) == 4 and base[:3] in {"COM", "LPT"} and base[3] in "123456789"


def _validate_relative_path(relative: str) -> None:
    if not relative or len(relative) > MAX_RELATIVE_PATH_CHARACTERS or "\\" in relative:
        raise ManifestError(f"invalid package path: {relative!r}")
    pure = PurePosixPath(relative)
    if pure.is_absolute() or relative != pure.as_posix() or any(part in {"", ".", ".."} for part in pure.parts):
        raise ManifestError(f"non-canonical package path: {relative!r}")
    if relative.casefold() == MANIFEST_NAME.casefold():
        raise ManifestError(f"the manifest cannot list itself: {relative!r}")
    for segment in pure.parts:
        if (
            len(segment) > MAX_SEGMENT_CHARACTERS
            or segment.endswith((" ", "."))
            or any(ord(character) < 32 or character in ':*?"<>|' for character in segment)
            or _is_reserved_device(segment)
        ):
            raise ManifestError(f"Windows-unsafe package path segment: {segment!r}")


def _ensure_regular_file(path: Path) -> os.stat_result:
    try:
        metadata = path.lstat()
    except OSError as exc:
        raise ManifestError(f"cannot inspect publish file: {path}") from exc
    if stat.S_ISLNK(metadata.st_mode) or not stat.S_ISREG(metadata.st_mode):
        raise ManifestError(f"publish tree contains a non-regular file: {path}")
    return metadata


def _iter_publish_files(root: Path) -> list[tuple[str, Path, os.stat_result]]:
    files: list[tuple[str, Path, os.stat_result]] = []
    casefolded_paths: set[str] = set()
    try:
        entries = sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix())
    except OSError as exc:
        raise ManifestError(f"cannot enumerate publish directory: {root}") from exc

    for path in entries:
        relative = path.relative_to(root).as_posix()
        try:
            metadata = path.lstat()
        except OSError as exc:
            raise ManifestError(f"cannot inspect publish entry: {path}") from exc
        if stat.S_ISLNK(metadata.st_mode):
            raise ManifestError(f"publish tree contains a symbolic link: {path}")
        if stat.S_ISDIR(metadata.st_mode):
            continue
        if relative.casefold() == MANIFEST_NAME.casefold():
            if relative != MANIFEST_NAME:
                raise ManifestError(f"case-ambiguous manifest path exists: {relative!r}")
            continue
        metadata = _ensure_regular_file(path)
        _validate_relative_path(relative)
        folded = relative.casefold()
        if folded in casefolded_paths:
            raise ManifestError(f"case-insensitive duplicate package path: {relative!r}")
        casefolded_paths.add(folded)
        files.append((relative, path, metadata))

    if not files:
        raise ManifestError("publish tree contains no package files")
    if len(files) > MAX_FILE_COUNT:
        raise ManifestError(f"publish tree contains more than {MAX_FILE_COUNT} files")
    return files


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                digest.update(block)
    except OSError as exc:
        raise ManifestError(f"cannot hash publish file: {path}") from exc
    return digest.hexdigest()


def build_manifest(root: Path, version: str) -> dict[str, object]:
    _validate_version(version)
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ManifestError(f"publish root is not a directory: {root}")

    files: list[dict[str, object]] = []
    total_bytes = 0
    for relative, path, metadata in _iter_publish_files(root):
        size = metadata.st_size
        if size < 0 or size > MAX_FILE_BYTES:
            raise ManifestError(f"package file exceeds its size limit: {relative}")
        total_bytes += size
        if total_bytes > MAX_PACKAGE_BYTES:
            raise ManifestError("publish tree exceeds the package size limit")
        files.append({"path": relative, "sizeBytes": size, "sha256": _sha256(path)})

    required = {"CodexUsageMonitor.exe", "CodexUsageMonitor.UpdaterHost.exe"}
    present = {entry["path"] for entry in files}
    missing = sorted(required - present)
    if missing:
        raise ManifestError(f"publish tree is missing required executable(s): {', '.join(missing)}")
    for entry in files:
        if entry["path"] in required and int(entry["sizeBytes"]) <= 0:
            raise ManifestError(f"required executable is empty: {entry['path']}")

    return {"schemaVersion": SCHEMA_VERSION, "version": version, "files": files}


def serialize_manifest(manifest: dict[str, object]) -> bytes:
    payload = json.dumps(
        manifest,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
    ).encode("utf-8")
    if not payload or len(payload) > MAX_SERIALIZED_BYTES:
        raise ManifestError("serialized update file manifest exceeds its size limit")
    return payload


def write_manifest(root: Path, version: str) -> Path:
    root = root.resolve(strict=True)
    destination = root / MANIFEST_NAME
    payload = serialize_manifest(build_manifest(root, version))
    temporary = root / f".{MANIFEST_NAME}.{os.getpid()}.tmp"
    try:
        with temporary.open("xb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)
    verify_manifest(root, version)
    return destination


def verify_manifest(root: Path, expected_version: str) -> dict[str, object]:
    root = root.resolve(strict=True)
    destination = root / MANIFEST_NAME
    try:
        raw = destination.read_bytes()
    except OSError as exc:
        raise ManifestError(f"cannot read generated manifest: {destination}") from exc
    if not raw or len(raw) > MAX_SERIALIZED_BYTES:
        raise ManifestError("generated manifest has an invalid size")
    try:
        document = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ManifestError("generated manifest is not valid UTF-8 JSON") from exc
    expected = build_manifest(root, expected_version)
    if document != expected:
        raise ManifestError("generated manifest does not match the publish tree")
    if raw != serialize_manifest(expected):
        raise ManifestError("generated manifest is not in canonical serialized form")
    return document


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    try:
        path = args.source.resolve(strict=True) / MANIFEST_NAME
        if args.verify_only:
            document = verify_manifest(args.source, args.version)
        else:
            path = write_manifest(args.source, args.version)
            document = verify_manifest(args.source, args.version)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        print(f"{path} | files={len(document['files'])} | sha256={digest}")
        return 0
    except (ManifestError, OSError) as exc:
        print(f"update-file-manifest: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
