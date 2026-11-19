"""Resolve the active offline guide from the release guide's tag selection."""

import argparse
from pathlib import Path
import re

VERSION = re.compile(Path(__file__).with_name('release-version.ere').read_text(encoding='ascii').strip())


def guide_path(release_doc, tag=None):
    versions = re.findall(r'^NEXT_VERSION=([^\s]+)', release_doc, re.MULTILINE)
    if len(versions) != 1:
        raise ValueError('Expected one NEXT_VERSION release selection')
    for label, value in (('NEXT_VERSION', versions[0]), ('release tag', tag)):
        if value is not None and not VERSION.fullmatch(value):
            raise ValueError(f'Invalid {label} {value!r}: expected vMAJOR.MINOR.PATCH without leading zeros, '
                             'optionally followed by a prerelease such as -beta.1; no +build suffix')
    if tag is not None and versions[0] != tag:
        raise ValueError(f'Release tag {tag!r} does not match documented NEXT_VERSION {versions[0]!r}')
    return f'docs/upgrade-{versions[0]}.md'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag', help='require the documented version to match this release tag')
    args = parser.parse_args()
    try:
        print(guide_path((Path(__file__).resolve().parents[2] / 'docs/releases.md').read_text(encoding='utf-8'), args.tag))
    except ValueError as error:
        parser.exit(1, f'{error}\n')
