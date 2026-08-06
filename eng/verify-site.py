#!/usr/bin/env python3
"""Validate the dependency-free public website and its local navigation."""
from __future__ import annotations

from html.parser import HTMLParser
from pathlib import Path
import subprocess
import sys
from urllib.parse import urlparse


ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "docs"


class PageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.links: list[str] = []
        self.ids: set[str] = set()
        self.h1_count = 0
        self.has_main = False
        self.has_description = False
        self.has_viewport = False
        self.scripts: list[str] = []
        self.stylesheets: list[str] = []
        self.media_assets: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        if values.get("id"):
            self.ids.add(values["id"] or "")
        if tag == "a" and values.get("href"):
            self.links.append(values["href"] or "")
        elif tag == "h1":
            self.h1_count += 1
        elif tag == "main":
            self.has_main = True
        elif tag == "meta" and values.get("name") == "description" and values.get("content"):
            self.has_description = True
        elif tag == "meta" and values.get("name") == "viewport" and values.get("content"):
            self.has_viewport = True
        elif tag == "script" and values.get("src"):
            self.scripts.append(values["src"] or "")
        elif tag == "link" and values.get("href"):
            relationships = (values.get("rel") or "").lower().split()
            if "stylesheet" in relationships:
                self.stylesheets.append(values["href"] or "")
            elif "icon" in relationships:
                self.media_assets.append(values["href"] or "")
        elif tag in {"audio", "img", "source", "video"} and values.get("src"):
            self.media_assets.append(values["src"] or "")


def validate_local_asset(page: Path, source: str, kind: str) -> list[str]:
    relative = page.relative_to(ROOT)
    parsed = urlparse(source)
    if parsed.scheme or parsed.netloc or source.startswith("//"):
        return [f"{relative} loads a remote {kind}: {source}"]
    target = (page.parent / parsed.path).resolve()
    try:
        target.relative_to(SITE.resolve())
    except ValueError:
        return [f"{relative} loads a {kind} outside the site: {source}"]
    if not target.is_file():
        return [f"{relative} has a missing {kind}: {source}"]
    return []


def validate_page(path: Path) -> list[str]:
    parser = PageParser()
    parser.feed(path.read_text(encoding="utf-8"))
    relative = path.relative_to(ROOT)
    errors: list[str] = []
    if parser.h1_count != 1:
        errors.append(f"{relative} must contain exactly one h1")
    if not parser.has_main:
        errors.append(f"{relative} is missing a main landmark")
    if not parser.has_viewport:
        errors.append(f"{relative} is missing a viewport declaration")
    if path.name != "404.html" and not parser.has_description:
        errors.append(f"{relative} is missing a meta description")
    if parser.stylesheets != ["styles.css"]:
        errors.append(f"{relative} must load only styles.css")
    if path.name == "index.html":
        required_home_ids = {"top", "benefits", "how-it-works", "download"}
        missing_ids = required_home_ids - parser.ids
        if missing_ids:
            errors.append(f"{relative} is missing required sections: {', '.join(sorted(missing_ids))}")
        if parser.scripts != ["experience.js"]:
            errors.append(f"{relative} must load only experience.js")
    elif parser.scripts:
        errors.append(f"{relative} must not load scripts")

    for source in parser.scripts:
        errors.extend(validate_local_asset(path, source, "script"))
    for source in parser.stylesheets:
        errors.extend(validate_local_asset(path, source, "stylesheet"))
    for source in parser.media_assets:
        errors.extend(validate_local_asset(path, source, "media asset"))

    for link in parser.links:
        parsed = urlparse(link)
        if parsed.scheme or parsed.netloc or link.startswith(("#", "mailto:")):
            continue
        target_name = parsed.path or path.name
        target = (path.parent / target_name).resolve()
        try:
            target.relative_to(SITE.resolve())
        except ValueError:
            errors.append(f"{relative} links outside the site: {link}")
            continue
        if not target.is_file():
            errors.append(f"{relative} has a missing local link: {link}")
    return errors


def main() -> int:
    allowed_files = {
        Path("index.html"), Path("privacy.html"), Path("support.html"),
        Path("code-signing.html"), Path("404.html"), Path("styles.css"),
        Path("experience.js"), Path("favicon.svg"), Path(".nojekyll"),
    }
    present_files = {
        path.relative_to(SITE)
        for path in SITE.rglob("*")
        if path.is_file()
    } if SITE.is_dir() else set()
    errors = [f"missing site file: {path.as_posix()}" for path in sorted(allowed_files - present_files)]
    errors.extend(
        f"unexpected site file: {path.as_posix()}"
        for path in sorted(present_files - allowed_files)
    )
    for path in sorted(SITE.glob("*.html")):
        errors.extend(validate_page(path))
    try:
        experience_tests = subprocess.run(
            ["node", "--test", ROOT / "eng" / "site-experience.test.js"],
            cwd=ROOT,
            capture_output=True,
            text=True,
        )
    except FileNotFoundError:
        errors.append("Node.js is required to verify the website experience")
    else:
        if experience_tests.returncode != 0:
            output = (experience_tests.stdout + experience_tests.stderr).strip()
            errors.append(f"website experience tests failed:\n{output}")
    if errors:
        print("Website verification failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1
    print(f"Website verification passed for {len(list(SITE.glob('*.html')))} pages and 5 experience checks.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
