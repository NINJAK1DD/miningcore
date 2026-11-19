"""Resolve the active offline guide from the release guide's tag selection."""

from pathlib import Path
import re


def guide_path(release_doc):
    versions = re.findall(r'^NEXT_VERSION=([^\s]+)', release_doc, re.MULTILINE)
    if len(versions) != 1 or not re.fullmatch(r'v\d+\.\d+\.\d+(?:-rc\.\d+)?', versions[0]):
        raise ValueError('Expected one NEXT_VERSION release selection')
    return f'docs/upgrade-{versions[0]}.md'


if __name__ == '__main__':
    print(guide_path((Path(__file__).resolve().parents[2] / 'docs/releases.md').read_text(encoding='utf-8')))
