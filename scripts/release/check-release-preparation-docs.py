#!/usr/bin/env python3
"""Fail-closed RC preparation contracts, independent of Python optimisation."""

from collections import Counter
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[2]
BASELINE = ROOT / 'scripts/release/fixtures/v0.4.0-rc.1-legacy-release-anchors.txt'
DOCUMENTS = ('README.md', 'docs/releases.md', 'docs/database.md',
             'docs/upgrade-v0.4.0-rc.1.md')
STABLE_BOUNDARY = 'The latest stable baseline remains v0.3.0 until a newer stable release is published.'
NO_SQL = 'No new SQL solely for this upgrade when the v0.3.0 prerequisites are installed.'


def prose(text):
    return ' '.join(re.sub(r'^> ?', '', text, flags=re.MULTILINE).split())


def validate(documents, baseline):
    errors = []

    def require(condition, message):
        if not condition:
            errors.append(message)

    expected_assignments = {
        'README.md': Counter({('MININGCORE_VERSION', 'v0.3.0'): 3}),
        'docs/releases.md': Counter({('MININGCORE_VERSION', 'v0.3.0'): 2,
                                     ('NEXT_VERSION', 'v0.4.0-rc.1'): 1,
                                     ('TAG', 'v0.4.0-rc.1'): 1}),
        'docs/database.md': Counter({('MININGCORE_VERSION', 'v0.4.0-rc.1'): 1}),
    }
    for name, expected in expected_assignments.items():
        actual = Counter(re.findall(
            r'^(?:export )?(MININGCORE_VERSION|TAG|NEXT_VERSION)=(\S+)',
            documents[name], re.MULTILINE))
        require(actual == expected, f'{name}: stale, missing or duplicated release assignment')
    for name in ('README.md', 'docs/releases.md'):
        require(STABLE_BOUNDARY in prose(documents[name]), f'{name}: missing stable baseline')

    release = documents['docs/releases.md']
    for heading in re.findall(r'^## Unreleased[^\n]*', release, re.MULTILINE):
        require(re.fullmatch(r'## Unreleased \(after v0\.4\.0-rc\.1\): \S.*', heading) is not None,
                f'Invalid next-cycle release heading: {heading}')

    # This baseline is independent of the current headings/aliases, so renaming
    # both together cannot silently change an already published external link.
    expected = [line for line in baseline.splitlines() if line and not line.startswith('#')]
    require(len(expected) == 33 and len(set(expected)) == 33, 'Invalid frozen historical-anchor baseline')
    actual = re.findall(r'^<a id="(unreleased[^"]*)"></a>$', release, re.MULTILINE)
    require(Counter(actual) == Counter(expected), 'Historical release anchors differ from the frozen baseline')
    pairs = re.findall(r'^<a id="(unreleased[^"]*)"></a>\n\n## v0\.4\.0-rc\.1: (.+)$',
                       release, re.MULTILINE)
    require(len(pairs) == 33, 'Historical anchors must precede their RC.1 headings')
    for alias, heading in pairs:
        suffix = re.sub(r'[^\w\- ]', '', heading.lower()).replace(' ', '-')
        require(alias in (f'unreleased-{suffix}', f'unreleased-after-v030-{suffix}'),
                f'Historical anchor does not match its destination: {alias}')

    upgrade = documents['docs/upgrade-v0.4.0-rc.1.md']
    for prefix in ('| Canonical BTC SOLO,', '| LTC/DOGE merged SOLO/SOLO,'):
        rows = [line for line in upgrade.splitlines() if line.startswith(prefix)]
        require(len(rows) == 1 and rows[0].split('|')[2].strip().startswith(NO_SQL),
                f'Incorrect unchanged SOLO migration row: {prefix}')
    require('plain non-merged PROP/PPLNS/PPLNSBF | No general new SQL migration.' in upgrade,
            'Missing non-merged pooled migration exception')
    require('merged-mining parent/aux accounting on PROP/PPLNS/PPLNSBF even without PPS' in upgrade,
            'Missing precise pooled-merged migration scope')
    for fragment in ('### Monitoring and log consumers', '## Container upgrades',
                     'src/Miningcore/Persistence/Postgres/Scripts/',
                     '`/app/migrations/` and `/app/BUILD-INFO`',
                     'Neither image automatically upgrades the database.',
                     'diagnosticFormatVersion', 'Disconnecting banned address @',
                     'Using PostgreSQL persistence ', 'v0.3.0 upgrade prerequisites'):
        require(fragment in upgrade, f'Upgrade guide is missing: {fragment}')
    require('XBT' not in upgrade, 'Use the distinct BTCB2B chain label in the upgrade guide')
    container = release.split('## Use the GitHub Container Registry image\n', 1)
    require(len(container) == 2 and 'new container installation only' in container[1],
            'Container installation must warn existing operators not to overwrite state')
    return errors


def main():
    documents = {name: (ROOT / name).read_text(encoding='utf-8') for name in DOCUMENTS}
    errors = validate(documents, BASELINE.read_text(encoding='utf-8'))
    if errors:
        print('\n'.join(errors), file=sys.stderr)
        return 1
    print('Release preparation contracts passed (stable defaults, next-cycle notes, frozen anchors and migration rows)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
