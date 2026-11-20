#!/usr/bin/env python3

"""Validate repository-local Markdown links and heading anchors."""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit
from xml.etree import ElementTree


REPO_ROOT = Path(__file__).resolve().parents[2]
DOCS_ROOT = REPO_ROOT / "docs"
EXAMPLES_ROOT = REPO_ROOT / "examples"
DOCS_INDEX = DOCS_ROOT / "README.md"
MARKDOWN_FILES = [
    REPO_ROOT / "README.md",
    REPO_ROOT / "ShareRelaysReadMe.md",
    *sorted(EXAMPLES_ROOT.rglob("*.md")),
    *sorted(DOCS_ROOT.rglob("*.md")),
]
FENCE = re.compile(r"^\s*(`{3,}|~{3,})(.*)$")
HEADING = re.compile(r"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$")
EXPLICIT_ANCHOR = re.compile(r"<(?:a\s+name|[^>]+\sid)=[\"']([^\"']+)[\"']", re.I)


def markdown_slug(text: str) -> str:
    text = re.sub(r"<[^>]+>", "", text)
    text = re.sub(r"[`*~]", "", text).strip().lower()
    text = re.sub(r"[^\w\- ]", "", text, flags=re.UNICODE)
    return text.replace(" ", "-")


def update_fence(line: str, current: tuple[str, int] | None) -> tuple[str, int] | None:
    match = FENCE.match(line)
    if not match:
        return current

    marker = match.group(1)
    trailing = match.group(2).strip()
    if current is None:
        return marker[0], len(marker)

    marker_character, minimum_length = current
    if marker[0] == marker_character and len(marker) >= minimum_length and not trailing:
        return None

    return current


def anchors_for(path: Path) -> set[str]:
    anchors: set[str] = set()
    counts: dict[str, int] = {}
    fence: tuple[str, int] | None = None

    for line in path.read_text(encoding="utf-8").splitlines():
        updated_fence = update_fence(line, fence)
        if updated_fence != fence:
            fence = updated_fence
            continue
        if fence:
            continue

        for explicit in EXPLICIT_ANCHOR.findall(line):
            anchors.add(explicit)

        match = HEADING.match(line)
        if not match:
            continue

        base = markdown_slug(match.group(2))
        duplicate_index = counts.get(base, 0)
        counts[base] = duplicate_index + 1
        anchors.add(base if duplicate_index == 0 else f"{base}-{duplicate_index}")

    return anchors


def markdown_tree(path: Path, *, extra=""):
    # Parse the links readers see, independently of the archive rewriter.
    command = ['cmark-gfm', '--to', 'xml', '--sourcepos', '--extension', 'table']
    try:
        xml = (subprocess.check_output(command, input=path.read_bytes() + extra.encode('utf-8'))
               if extra else subprocess.check_output([*command, str(path)]))
    except FileNotFoundError:
        raise SystemExit('Markdown validation requires cmark-gfm. On Ubuntu run: sudo apt install cmark-gfm') from None
    return ElementTree.fromstring(xml)


def local_targets(path: Path):
    # cmark omits unused reference definitions. Append a synthetic use of
    # each candidate label; cmark still decides whether it's really defined
    # (and handles multiline destinations, titles, escapes and code blocks).
    lines = path.read_text(encoding='utf-8').splitlines()
    candidates = [(line, match[1]) for line, text in enumerate(lines, 1)
                  if (match := re.match(r'^[ \t]*(?:>[ \t]*)*\[((?:\\.|[^\[\]\\])+)\]:', text))]
    excluded = set()
    if candidates:
        # Use the real block spans, including nested/indented code and HTML
        # comments. A definition-shaped example must not borrow a real label.
        for node in markdown_tree(path).iter():
            if node.tag.rsplit('}', 1)[-1] in ('code_block', 'html_block'):
                pos = re.fullmatch(r'(\d+):\d+-(\d+):\d+', node.attrib.get('sourcepos', ''))
                if pos:
                    excluded.update(range(int(pos[1]), int(pos[2]) + 1))
    probes = []
    extra = []
    for line, label in candidates:
        if line not in excluded:
            extra.append(f'\n\n[probe-{len(probes)}][{label}]')
            probes.append(line)
    # If the suffix is swallowed by a fence or HTML comment, do not silently
    # accept an empty probe inventory. Never validate this artificial link.
    end_marker = '#miningcore-end-of-document-probe'
    extra.append(f'\n\n[end-of-document]({end_marker})\n')
    saw_end = False
    tree = markdown_tree(path, extra=''.join(extra))
    for node in tree.iter():
        if node.tag.rsplit('}', 1)[-1] in ('link', 'image'):
            # cmark can locate the link itself on its closing line. Its
            # label children retain the opening line of wrapped text.
            positions = [int(child.attrib['sourcepos'].split(':', 1)[0])
                         for child in node.iter() if 'sourcepos' in child.attrib]
            line = min((position for position in positions if position), default=0)
            if line > len(lines):
                if node.attrib['destination'] == end_marker and ''.join(node.itertext()).strip() == 'end-of-document':
                    saw_end = True
                    continue
                label = re.fullmatch(r'probe-(\d+)', ''.join(node.itertext()).strip())
                if not label:
                    continue
                line = probes[int(label[1])]
            yield line, node.attrib['destination']
    if not saw_end:
        raise SystemExit(f'{path}: Markdown validation probes were hidden; check for an unclosed code fence or HTML comment')


def main() -> int:
    errors: list[str] = []
    anchor_cache: dict[Path, set[str]] = {}
    indexed_guides: set[Path] = set()

    if markdown_slug("`payment_batches` recovery") != "payment_batches-recovery":
        errors.append("internal slug regression: underscores must match GitHub heading anchors")
    if update_fence("```", ("~", 3)) != ("~", 3):
        errors.append("internal fence regression: a mismatched marker must not close a fence")

    for source in MARKDOWN_FILES:
        for line, raw_target in local_targets(source):
            target = unquote(raw_target)
            parsed = urlsplit(raw_target)
            if parsed.scheme or parsed.netloc:
                continue

            relative_path = unquote(parsed.path)
            destination = source.resolve() if not relative_path else (source.parent / relative_path).resolve()
            try:
                destination.relative_to(REPO_ROOT)
            except ValueError:
                errors.append(f"{source.relative_to(REPO_ROOT)}:{line}: link leaves repository: {target}")
                continue

            if not destination.exists():
                errors.append(f"{source.relative_to(REPO_ROOT)}:{line}: missing target: {target}")
                continue

            if source == DOCS_INDEX and destination.suffix.lower() == ".md":
                indexed_guides.add(destination)

            if not parsed.fragment or destination.is_dir() or destination.suffix.lower() != ".md":
                continue

            if destination not in anchor_cache:
                anchor_cache[destination] = anchors_for(destination)
            if unquote(parsed.fragment) not in anchor_cache[destination]:
                errors.append(
                    f"{source.relative_to(REPO_ROOT)}:{line}: missing anchor "
                    f"#{parsed.fragment} in {destination.relative_to(REPO_ROOT)}"
                )

    for guide in sorted(DOCS_ROOT.glob("*.md")):
        if guide == DOCS_INDEX:
            continue
        if guide.resolve() not in indexed_guides:
            errors.append(
                f"{guide.relative_to(REPO_ROOT)}: guide is missing from docs/README.md"
            )

    if errors:
        print("Markdown link validation failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print(f"Validated local links and anchors in {len(MARKDOWN_FILES)} Markdown files")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
