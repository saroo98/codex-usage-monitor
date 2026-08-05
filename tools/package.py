from __future__ import annotations

import hashlib
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app"
DIST = ROOT / "dist"
LICENSE = ROOT / "LICENSE"
FIXED_TIME = (2020, 1, 1, 0, 0, 0)


def iter_public_files() -> list[Path]:
    excluded = {"__pycache__", ".pytest_cache", ".mypy_cache"}
    files: list[Path] = []
    for path in APP.rglob("*"):
        if not path.is_file():
            continue
        if any(part in excluded for part in path.parts):
            continue
        if path.suffix.lower() in {".pyc", ".pyo"}:
            continue
        files.append(path)
    return sorted(files, key=lambda item: item.as_posix().lower())


def add_bytes(archive: zipfile.ZipFile, name: str, data: bytes) -> None:
    info = zipfile.ZipInfo(name, FIXED_TIME)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o100644 << 16
    archive.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def main() -> None:
    version = (APP / "VERSION").read_text(encoding="utf-8-sig").strip()
    if not version:
        raise SystemExit("app/VERSION is empty")
    if not LICENSE.is_file():
        raise SystemExit("LICENSE is missing")

    DIST.mkdir(parents=True, exist_ok=True)
    output = DIST / f"Usage-Monitor-for-Codex-{version}-Windows.zip"

    with zipfile.ZipFile(output, "w") as archive:
        for path in iter_public_files():
            relative = path.relative_to(APP).as_posix()
            add_bytes(archive, f"CodexUsageNotifier/{relative}", path.read_bytes())
        add_bytes(archive, "CodexUsageNotifier/LICENSE", LICENSE.read_bytes())

    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    checksums = DIST / "SHA256SUMS.txt"
    checksums.write_text(f"{digest} *{output.name}\n", encoding="ascii", newline="\n")
    print(output)
    print(digest)


if __name__ == "__main__":
    main()
