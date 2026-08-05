#!/usr/bin/env python3
"""Create and verify a deterministic ZIP from a directory tree."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path, PurePosixPath
import stat
import sys
import zipfile

FIXED_TIMESTAMP = (2000, 1, 1, 0, 0, 0)


def normalize_entry(prefix: str, relative: Path) -> str:
    parts = [part for part in PurePosixPath(prefix, relative.as_posix()).parts if part not in ("", ".")]
    if not parts or any(part == ".." for part in parts):
        raise ValueError(f"unsafe archive path: {prefix}/{relative}")
    return "/".join(parts)


def iter_files(root: Path) -> list[Path]:
    excluded_names = {"Thumbs.db", ".DS_Store"}
    files: list[Path] = []
    casefolded: set[str] = set()
    for path in root.rglob("*"):
        if path.is_symlink():
            raise ValueError(f"symbolic links are not allowed in release archives: {path}")
        if not path.is_file() or path.name in excluded_names:
            continue
        relative = path.relative_to(root).as_posix()
        folded = relative.casefold()
        if folded in casefolded:
            raise ValueError(f"case-insensitive duplicate source path: {relative}")
        casefolded.add(folded)
        files.append(path)
    return sorted(files, key=lambda item: item.relative_to(root).as_posix())


def write_zip(source: Path, output: Path, prefix: str) -> None:
    source = source.resolve(strict=True)
    output = output.resolve(strict=False)
    try:
        output.relative_to(source)
    except ValueError:
        pass
    else:
        raise ValueError("archive output must be outside the source directory")
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    temporary.unlink(missing_ok=True)
    entries: set[str] = set()
    with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in iter_files(source):
            relative = path.relative_to(source)
            name = normalize_entry(prefix, relative)
            folded = name.casefold()
            if folded in entries:
                raise ValueError(f"duplicate archive path: {name}")
            entries.add(folded)
            info = zipfile.ZipInfo(name, FIXED_TIMESTAMP)
            info.create_system = 3
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)
    os.replace(temporary, output)


def verify_zip(path: Path) -> tuple[int, int, str]:
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    names: set[str] = set()
    total = 0
    with zipfile.ZipFile(path, "r") as archive:
        bad = archive.testzip()
        if bad is not None:
            raise ValueError(f"corrupt ZIP member: {bad}")
        for entry in archive.infolist():
            pure = PurePosixPath(entry.filename)
            if pure.is_absolute() or ".." in pure.parts:
                raise ValueError(f"unsafe ZIP member: {entry.filename}")
            folded = entry.filename.casefold()
            if folded in names:
                raise ValueError(f"duplicate ZIP member: {entry.filename}")
            names.add(folded)
            total += entry.file_size
            with archive.open(entry, "r") as stream:
                while stream.read(1024 * 1024):
                    pass
    if not names:
        raise ValueError("ZIP contains no files")
    return len(names), total, digest


def compare_zips(first: Path, second: Path) -> list[str]:
    """Return concise member-level differences between two ZIP archives."""
    with zipfile.ZipFile(first, "r") as left, zipfile.ZipFile(second, "r") as right:
        left_entries = {entry.filename: entry for entry in left.infolist()}
        right_entries = {entry.filename: entry for entry in right.infolist()}
        differences: list[str] = []
        for name in sorted(left_entries.keys() | right_entries.keys()):
            if name not in left_entries:
                differences.append(f"only-second: {name}")
                continue
            if name not in right_entries:
                differences.append(f"only-first: {name}")
                continue
            left_info = left_entries[name]
            right_info = right_entries[name]
            left_bytes = left.read(name)
            right_bytes = right.read(name)
            if left_bytes != right_bytes:
                differences.append(
                    f"content: {name} "
                    f"first={hashlib.sha256(left_bytes).hexdigest()} "
                    f"second={hashlib.sha256(right_bytes).hexdigest()}"
                )
            elif (
                left_info.date_time != right_info.date_time
                or left_info.external_attr != right_info.external_attr
                or left_info.compress_type != right_info.compress_type
            ):
                differences.append(f"metadata: {name}")
        return differences


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--verify", type=Path)
    parser.add_argument("--compare", nargs=2, type=Path, metavar=("FIRST", "SECOND"))
    parser.add_argument("--prefix", default="CodexUsageMonitor")
    args = parser.parse_args()

    if args.compare is not None:
        if args.source is not None or args.output is not None or args.verify is not None:
            parser.error("--compare cannot be combined with --source, --output, or --verify")
        differences = compare_zips(args.compare[0], args.compare[1])
        if differences:
            print("ZIP archives differ:")
            for difference in differences:
                print(f"  {difference}")
            return 1
        print("ZIP archives are byte-equivalent by member content and normalized metadata")
        return 0

    if args.verify is not None:
        if args.source is not None or args.output is not None:
            parser.error("--verify cannot be combined with --source or --output")
        count, total, digest = verify_zip(args.verify)
        print(f"{args.verify} | entries={count} | uncompressed={total} | sha256={digest}")
        return 0
    if args.source is None or args.output is None:
        parser.error("--source and --output are required when --verify is not used")

    write_zip(args.source, args.output, args.prefix)
    count, total, digest = verify_zip(args.output)
    print(f"{args.output} | entries={count} | uncompressed={total} | sha256={digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
