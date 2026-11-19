"""Render the offline installation guide or the public release-note preamble."""

import argparse
from pathlib import Path
import posixpath
import re
from urllib.parse import quote, unquote, urlsplit
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]


def map_links(text, transform):
    """Transform guide destinations only, preserving literal examples and link titles."""
    tokens = re.compile(
        r'(?P<code>(?<!`)(?P<ticks>`+)(?!`).*?(?<!`)(?P=ticks)(?!`))'
        r'|(?P<prefix>\]\(|^ {0,3}\[[^\]\n]+\]:[ \t]*)(?:<(?P<angle>[^>\n]+)>|(?P<bare>(?:[^\s()<>]|\([^\s()]*\))+))',
        re.MULTILINE | re.DOTALL)

    def replace(match):
        if match['code'] is not None:
            return match[0]
        if match['angle'] is not None:
            return match['prefix'] + '<' + transform(match['angle']) + '>'
        return match['prefix'] + transform(match['bare'])

    lines, prose = [], []
    fence = None
    for line in text.splitlines(keepends=True):
        marker = re.match(r'^ {0,3}(`{3,}|~{3,})(.*)$', line)
        if not (marker or fence or not line.strip() or line.expandtabs(4).startswith('    ')):
            prose.append(line)
            continue
        lines.append(tokens.sub(replace, ''.join(prose)))
        prose.clear()
        if marker and fence is None:
            fence = marker[1]
        elif marker and marker[1].startswith(fence or '!') and not marker[2].strip():
            fence = None
        lines.append(line)
    lines.append(tokens.sub(replace, ''.join(prose)))
    return ''.join(lines)


def install(root, source_url):
    root = root.resolve()

    def destination(target):
        parsed = urlsplit(target)
        if not parsed.path or parsed.scheme or parsed.netloc:
            return target
        path = posixpath.normpath(posixpath.join('docs', unquote(parsed.path)))
        local = (root / path).resolve()
        if path.startswith('/') or not local.is_relative_to(root):
            raise ValueError(f'Guide target leaves package: {target}')
        url_path = quote(path, safe="/:@!$&'*+,;=-._~")
        url = url_path if local.exists() else f'{source_url}/{url_path}'
        return parsed._replace(path=url).geturl()

    text = (root / 'docs/releases.md').read_text(encoding='utf-8')
    (root / 'INSTALL.md').write_text(map_links(text, destination), encoding='utf-8')


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
