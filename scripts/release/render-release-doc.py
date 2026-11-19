"""Render the offline installation guide or the public release-note preamble."""

import argparse
from pathlib import Path
import posixpath
import re
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]


def install(root, source_url):
    # Rebase links from docs/releases.md to the package root. Prefer shipped
    # files offline; repository-only resources must refer to this build's tree.
    def link(match):
        prefix, target = match.groups()
        parsed = urlsplit(target)
        if not parsed.path or parsed.scheme or parsed.netloc:
            return match[0]
        path = posixpath.normpath(posixpath.join('docs', parsed.path))
        destination = path if (root / path).exists() else f'{source_url}/{path}'
        return prefix + parsed._replace(path=destination).geturl()

    lines = []
    fence = None
    for line in (root / 'docs/releases.md').read_text(encoding='utf-8').splitlines(keepends=True):
        marker = re.match(r'^\s*(`{3,}|~{3,})(.*)$', line)
        if marker and fence is None:
            fence = marker[1]
        elif marker and marker[1].startswith(fence or '!') and not marker[2].strip():
            fence = None
        elif fence is None:
            line = re.sub(r'(\]\()([^\s)>]+)', link, line)
            line = re.sub(r'^(\s*\[[^\]]+\]:\s*<?)([^\s>]+)', link, line)
        lines.append(line)
    (root / 'INSTALL.md').write_text(''.join(lines), encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=('install', 'notes'))
    parser.add_argument('commit')
    parser.add_argument('repository')
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}(?:[0-9a-f]{24})?', args.commit):
        parser.error('expected an immutable source commit')
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', args.repository):
        parser.error('expected owner/repository')
    url = f'https://github.com/{args.repository}/blob/{args.commit}'
    if args.mode == 'install':
        install(args.output, url)
    else:
        floor = ET.parse(ROOT / 'src/Miningcore/RuntimeDependencyPolicy.props').findtext('.//MiningcoreRuntimeMinimum')
        if not floor or not re.fullmatch(r'10\.0\.\d+', floor):
            parser.error('invalid .NET runtime policy')
        args.output.write_text(
            'Use the Linux x64 archive matching your host: Ubuntu 26.04 (primary) or Ubuntu 22.04 '
            '(compatibility). Ubuntu 24.04 is a source-build target.\n\n'
            f'Install both .NET and ASP.NET Core {floor} or later within .NET 10. '
            'Read the packaged `INSTALL.md` and complete the applicable database steps before startup.\n\n'
            f'[Installation and upgrade guide for this build]({url}/docs/releases.md)\n', encoding='utf-8')
