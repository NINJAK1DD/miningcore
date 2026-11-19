"""Check the guide in the extracted archive, not just the source Markdown."""

from pathlib import Path
import runpy
import sys
from urllib.parse import urlsplit, unquote

helpers = runpy.run_path(str(Path(__file__).with_name('test-doc-links.py')))
root = Path(sys.argv[1]).resolve()
guide = root / 'INSTALL.md'
targets = {target for _, target in helpers['local_targets'](guide)}
if 'docs/upgrade-v0.4.0-rc.2.md' not in targets:
    sys.exit('Packaged INSTALL.md lacks the offline RC.2 route')
for raw in targets:
    parsed = urlsplit(raw)
    if parsed.scheme or parsed.netloc:
        continue
    target = (root / unquote(parsed.path)).resolve() if parsed.path else guide
    if not target.is_relative_to(root) or not target.exists():
        sys.exit(f'Missing packaged INSTALL.md target: {raw}')
    if parsed.fragment and target.suffix == '.md' and unquote(parsed.fragment) not in helpers['anchors_for'](target):
        sys.exit(f'Missing packaged INSTALL.md anchor: {raw}')
