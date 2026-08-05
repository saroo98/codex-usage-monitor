from __future__ import annotations

import hashlib
import shutil
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


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    version = (APP / "VERSION").read_text(encoding="utf-8-sig").strip()
    if not version:
        raise SystemExit("app/VERSION is empty")
    if not LICENSE.is_file():
        raise SystemExit("LICENSE is missing")

    DIST.mkdir(parents=True, exist_ok=True)
    output = DIST / f"Usage-Monitor-for-Codex-{version}-Windows.zip"
    backup = DIST / f"Usage-Monitor-for-Codex-{version}-Windows-BACKUP.zip"

    with zipfile.ZipFile(output, "w") as archive:
        for path in iter_public_files():
            relative = path.relative_to(APP).as_posix()
            add_bytes(archive, f"CodexUsageNotifier/{relative}", path.read_bytes())
        add_bytes(archive, "CodexUsageNotifier/LICENSE", LICENSE.read_bytes())

    shutil.copyfile(output, backup)

    artifacts = (output, backup)
    lines = [f"{sha256(path)} *{path.name}" for path in artifacts]
    checksums = DIST / "SHA256SUMS.txt"
    checksums.write_text("\n".join(lines) + "\n", encoding="ascii", newline="\n")

    for path in artifacts:
        print(f"{path} ({path.stat().st_size} bytes)")
        print(sha256(path))


if __name__ == "__main__":
    main()
