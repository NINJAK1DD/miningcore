"""Verify the reader-visible link destinations and examples in an extracted archive."""

from pathlib import Path
import runpy
import sys
from urllib.parse import unquote, urlsplit

helpers = runpy.run_path(str(Path(__file__).with_name('test-doc-links.py')))
guide_path = runpy.run_path(str(Path(__file__).with_name('current-upgrade-guide.py')))['guide_path']


def check(root, source_url):
    root = root.resolve()
    source = root / 'docs/releases.md'
    guide = root / 'INSTALL.md'
    required = guide_path(source.read_text(encoding='utf-8'))
    anchors = {}

    def inventory(document):
        nodes = []
        for node in helpers['markdown_tree'](document).iter():
            kind = node.tag.rsplit('}', 1)[-1]
            if kind in ('text', 'code', 'code_block'):
                nodes.append((kind, node.text))
            elif kind in ('link', 'image'):
                dest = urlsplit(node.attrib['destination'])
                if dest.scheme or dest.netloc:
                    nodes.append((kind, dest._replace(path=unquote(dest.path)).geturl()))
                    continue
                path = unquote(dest.path)
                target = (document.parent / path).resolve() if path else document
                relative = target.relative_to(root).as_posix()
                if not target.exists():
                    if document == guide:
                        raise ValueError(f'Missing packaged INSTALL.md target: {relative}')
                    path = f'{source_url}/{relative}'
                elif path:
                    path = relative
                if target.is_file() and target.suffix == '.md' and dest.fragment:
                    if target not in anchors:
                        anchors[target] = helpers['anchors_for'](target)
                    if unquote(dest.fragment) not in anchors[target]:
                        raise ValueError(f'Missing anchor: {relative}#{dest.fragment}')
                nodes.append((kind, dest._replace(path=path).geturl()))
        return nodes

    expected, actual = inventory(source), inventory(guide)
    if expected != actual:
        raise ValueError('Packaged INSTALL.md changed rendered text, a link destination or a literal example')
    if ('link', required) not in actual:
        raise ValueError(f'Packaged INSTALL.md lacks the offline route: {required}')


if __name__ == '__main__':
    check(Path(sys.argv[1]), sys.argv[2])
