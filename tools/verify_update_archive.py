#!/usr/bin/env python3
"""Verify a portable updater payload ZIP against its embedded file manifest."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import PurePosixPath, Path
import re
import sys
import zipfile

MANIFEST_NAME = "update-files.json"
REQUIRED_FILES = {"CodexUsageMonitor.exe", "CodexUsageMonitor.UpdaterHost.exe"}
MAX_MANIFEST_BYTES = 512 * 1024
MAX_FILES = 4096
MAX_MEMBER_BYTES = 512 * 1024 * 1024
MAX_TOTAL_BYTES = 1024 * 1024 * 1024
HEX_64 = re.compile(r"^[0-9a-f]{64}$")


class ArchiveError(ValueError):
    pass


def _safe_member_name(name: str) -> str:
    pure = PurePosixPath(name)
    if (
        not name
        or name.endswith("/")
        or pure.is_absolute()
        or name != pure.as_posix()
        or any(part in {"", ".", ".."} for part in pure.parts)
        or "\\" in name
    ):
        raise ArchiveError(f"unsafe or non-canonical ZIP member: {name!r}")
    return name


def _read_member(archive: zipfile.ZipFile, info: zipfile.ZipInfo) -> bytes:
    if info.file_size < 0 or info.file_size > MAX_MEMBER_BYTES:
        raise ArchiveError(f"ZIP member exceeds its size limit: {info.filename}")
    with archive.open(info, "r") as stream:
        data = stream.read(MAX_MEMBER_BYTES + 1)
    if len(data) != info.file_size or len(data) > MAX_MEMBER_BYTES:
        raise ArchiveError(f"ZIP member size is inconsistent: {info.filename}")
    return data


def verify(path: Path, expected_version: str | None) -> tuple[int, int, str]:
    path = path.resolve(strict=True)
    if not path.is_file() or path.stat().st_size <= 0:
        raise ArchiveError("update archive is missing or empty")

    archive_digest = hashlib.sha256(path.read_bytes()).hexdigest()
    try:
        archive = zipfile.ZipFile(path, "r")
    except (OSError, zipfile.BadZipFile) as exc:
        raise ArchiveError("update archive is not a valid ZIP") from exc

    with archive:
        bad = archive.testzip()
        if bad is not None:
            raise ArchiveError(f"update archive contains a corrupt member: {bad}")
        infos: dict[str, zipfile.ZipInfo] = {}
        folded: set[str] = set()
        total = 0
        for info in archive.infolist():
            name = _safe_member_name(info.filename)
            if info.is_dir():
                raise ArchiveError(f"explicit directory entries are not allowed: {name}")
            if name.casefold() in folded:
                raise ArchiveError(f"case-insensitive duplicate ZIP member: {name}")
            folded.add(name.casefold())
            infos[name] = info
            total += info.file_size
            if total > MAX_TOTAL_BYTES:
                raise ArchiveError("update archive exceeds its uncompressed size limit")
        if len(infos) == 0 or len(infos) > MAX_FILES + 1:
            raise ArchiveError("update archive contains an invalid number of files")
        missing = (REQUIRED_FILES | {MANIFEST_NAME}) - infos.keys()
        if missing:
            raise ArchiveError(f"update archive is missing required root file(s): {', '.join(sorted(missing))}")

        manifest_info = infos[MANIFEST_NAME]
        if manifest_info.file_size <= 0 or manifest_info.file_size > MAX_MANIFEST_BYTES:
            raise ArchiveError("embedded update file manifest has an invalid size")
        try:
            manifest = json.loads(_read_member(archive, manifest_info))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ArchiveError("embedded update file manifest is invalid JSON") from exc
        if not isinstance(manifest, dict) or set(manifest) != {"schemaVersion", "version", "files"}:
            raise ArchiveError("embedded update file manifest has an invalid schema")
        if manifest["schemaVersion"] != 1 or not isinstance(manifest["version"], str):
            raise ArchiveError("embedded update file manifest metadata is invalid")
        if expected_version is not None and manifest["version"] != expected_version:
            raise ArchiveError("embedded update file manifest version does not match the release")
        entries = manifest["files"]
        if not isinstance(entries, list) or not (1 <= len(entries) <= MAX_FILES):
            raise ArchiveError("embedded update file manifest has an invalid file count")

        declared: dict[str, tuple[int, str]] = {}
        previous: str | None = None
        declared_folded: set[str] = set()
        for entry in entries:
            if not isinstance(entry, dict) or set(entry) != {"path", "sizeBytes", "sha256"}:
                raise ArchiveError("embedded update file manifest contains an invalid entry")
            name = _safe_member_name(entry["path"] if isinstance(entry.get("path"), str) else "")
            size = entry.get("sizeBytes")
            digest = entry.get("sha256")
            if (
                name == MANIFEST_NAME
                or not isinstance(size, int)
                or isinstance(size, bool)
                or size < 0
                or size > MAX_MEMBER_BYTES
                or not isinstance(digest, str)
                or not HEX_64.fullmatch(digest)
                or (previous is not None and previous >= name)
                or name.casefold() in declared_folded
            ):
                raise ArchiveError("embedded update file manifest contains a non-canonical entry")
            declared[name] = (size, digest)
            declared_folded.add(name.casefold())
            previous = name

        actual_names = set(infos) - {MANIFEST_NAME}
        if set(declared) != actual_names:
            raise ArchiveError("archive members do not exactly match the embedded update file manifest")
        if not REQUIRED_FILES.issubset(declared):
            raise ArchiveError("embedded update file manifest omits a required executable")

        for name, (expected_size, expected_digest) in declared.items():
            info = infos[name]
            if info.file_size != expected_size:
                raise ArchiveError(f"archive member size does not match manifest: {name}")
            digest = hashlib.sha256()
            with archive.open(info, "r") as stream:
                while block := stream.read(1024 * 1024):
                    digest.update(block)
            if digest.hexdigest() != expected_digest:
                raise ArchiveError(f"archive member digest does not match manifest: {name}")

    return len(infos), total, archive_digest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", required=True, type=Path)
    parser.add_argument("--version")
    args = parser.parse_args()
    try:
        count, total, digest = verify(args.archive, args.version)
        print(f"{args.archive} | entries={count} | uncompressed={total} | sha256={digest}")
        return 0
    except (ArchiveError, OSError) as exc:
        print(f"update-archive: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
