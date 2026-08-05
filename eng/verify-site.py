#!/usr/bin/env python3
"""Validate the dependency-free public website and its local navigation."""
from __future__ import annotations

from html.parser import HTMLParser
from pathlib import Path
import sys
from urllib.parse import urlparse


ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "docs"


class PageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.links: list[str] = []
        self.h1_count = 0
        self.has_main = False
        self.has_description = False
        self.has_viewport = False
        self.external_scripts: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
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
            self.external_scripts.append(values["src"] or "")


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
    if parser.external_scripts:
        errors.append(f"{relative} loads external scripts")

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
    required = {
        "index.html", "privacy.html", "support.html", "code-signing.html", "404.html",
        "styles.css", "favicon.svg", ".nojekyll",
    }
    present = {path.name for path in SITE.iterdir() if path.is_file()} if SITE.is_dir() else set()
    errors = [f"missing site file: {name}" for name in sorted(required - present)]
    for path in sorted(SITE.glob("*.html")):
        errors.extend(validate_page(path))
    if errors:
        print("Website verification failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1
    print(f"Website verification passed for {len(list(SITE.glob('*.html')))} pages.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
