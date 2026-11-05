#!/usr/bin/env python3
"""Fail-closed RC preparation contracts, independent of Python optimisation."""

from collections import Counter
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[2]
BASELINE = ROOT / 'scripts/release/fixtures/v0.4.0-rc.1-legacy-release-anchors.txt'
DOCUMENTS = ('README.md', 'docs/releases.md', 'docs/database.md',
             'docs/upgrade-v0.4.0-rc.1.md', 'docs/bitcoin-minimum-difficulty.md',
             'docs/pps-arithmetic-migration.md', 'docs/troubleshooting.md')
STABLE_BOUNDARY = 'The latest stable baseline remains v0.3.0 until a newer stable release is published.'
NO_SQL = 'No new SQL solely for this upgrade when the v0.3.0 prerequisites are installed.'


def prose(text):
    return ' '.join(re.sub(r'^> ?', '', text, flags=re.MULTILINE).split())


def headings(text):
    """Read ATX and setext headings, ignoring fenced and indented examples."""
    fence = None
    paragraph = []
    for line in text.splitlines():
        marker = re.match(r'^ {0,3}(`{3,}|~{3,})(.*)$', line)
        if marker:
            paragraph = []
            delimiter, trailing = marker.groups()
            if fence is None:
                fence = (delimiter[0], len(delimiter))
            elif delimiter[0] == fence[0] and len(delimiter) >= fence[1] and not trailing.strip():
                fence = None
            continue
        if fence is not None:
            continue
        underline = re.fullmatch(r' {0,3}(=+|-+)[ \t]*', line)
        if underline:
            if paragraph:
                yield ('setext-1' if underline[1].startswith('=') else 'setext-2', ' '.join(paragraph))
            paragraph = []
            continue
        heading = re.match(r'^ {0,3}(#{1,6})[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$', line)
        if heading:
            yield heading.groups()
            paragraph = []
        elif not line.strip() or line.startswith(('    ', '\t', '>')):
            paragraph = []
        else:
            paragraph.append(line.strip())


def console_blocks(text, needle):
    return [block for block in re.findall(
        r'^ {0,3}```console\n(.*?)^ {0,3}```[ \t]*$', text, re.MULTILINE | re.DOTALL)
        if needle in block]


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
    for level, title in headings(release):
        if re.match(r'Unreleased\b', title, re.IGNORECASE):
            require(level == '##' and re.fullmatch(
                r'Unreleased \(after v0\.4\.0-rc\.1\): \S.*', title) is not None,
                f'Invalid next-cycle release heading: {level} {title}')
    for fragment in ('For a stable release, put the version flip in the final release-preparation commit',
                     'so its README and packaged INSTALL.md select that same stable version.',
                     'previous published stable tag\'s README as the runnable fallback.',
                     'Use wording that remains true before and after publication'):
        require(fragment in prose(release), f'Missing stable-release policy: {fragment}')
    tag_blocks = console_blocks(release, 'RELEASE_COMMIT=')
    require(len(tag_blocks) == 1, 'Expected one exact-commit release tagging example')
    if len(tag_blocks) == 1:
        for fragment in ('RELEASE_COMMIT=REPLACE_WITH_REVIEWED_MERGE_SHA',
                         'git merge-base --is-ancestor "$RELEASE_COMMIT" origin/dev',
                         'git tag -s "$NEXT_VERSION" "$RELEASE_COMMIT" -m',
                         'echo "STOP: select the reviewed full commit SHA reachable from origin/dev" >&2\n  false'):
            require(fragment in tag_blocks[0], f'Unsafe release target example: missing {fragment}')
    for fragment in ('If tag creation succeeded and only the push failed',
                     '`git rev-parse "${NEXT_VERSION}^{commit}"` exactly equals the recorded `RELEASE_COMMIT`',
                     'retry only `git push origin "$NEXT_VERSION"`',
                     'Never re-create, move or force-push an existing local or remote release tag.'):
        require(fragment in prose(release), f'Missing safe tag-push recovery guidance: {fragment}')
    require('The cumulative example below deliberately targets v0.4.0-rc.1' in
            prose(documents['docs/database.md']), 'Database example must explain its RC-specific target')
    require('docs/upgrade-v0.4.0-rc.1.md#container-upgrades' in documents['README.md'],
            'README container upgrade warning must link directly to the upgrade path')

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
    audit_guide = prose(documents['docs/bitcoin-minimum-difficulty.md'])
    for fragment in ('**The full audit is the default.**',
                     'Never automatically fall back to the shares-only script.',
                     'Their absence alone does not establish eligibility.',
                     '**never** been enabled in this database',
                     'cannot clear missing PPS evidence.',
                     'audit-legacy-settlement-shares.sql'):
        require(fragment in audit_guide, f'Missing historical-audit scope boundary: {fragment}')
    require('a failed full audit must not trigger an automatic fallback.' in prose(upgrade),
            'The RC guide must preserve the full financial audit boundary')
    for name in ('docs/database.md', 'docs/releases.md'):
        require('pps-arithmetic-migration.md#explicit-one-way-activation' in documents[name],
                f'{name}: missing standalone accounting upgrade route')
        require('missing earlier prerequisites' in prose(documents[name]),
                f'{name}: cumulative migration must be scoped to missing prerequisites')
    upgrade_blocks = console_blocks(release, 'MININGCORE_UPGRADE_READY=')
    for name in ('docs/database.md', 'docs/pps-arithmetic-migration.md'):
        migration = '"$MININGCORE_CANDIDATE_DIR/migrations/add_pps_arithmetic_version.sql"'
        preflight = '-f "$MININGCORE_CANDIDATE_DIR/scripts/release/check-pps-arithmetic-ownership.sql" &&'
        blocks = console_blocks(documents[name], migration)
        require(len(blocks) == 1, f'{name}: expected one manual standalone migration command')
        if len(blocks) == 1:
            block = blocks[0]
            require(block.count(preflight) == 1 and block.index(preflight) < block.index(migration),
                    f'{name}: ownership preflight must gate the standalone migration')
            require(len(re.findall(r'\bpsql(?=\s|$)', block)) ==
                    block.count('psql -X -v ON_ERROR_STOP=1') == 2,
                    f'{name}: both manual SQL commands must fail closed and ignore psql startup files')
    require(len(upgrade_blocks) == 1, 'Expected one explicitly selected archive upgrade example')
    if len(upgrade_blocks) == 1:
        for fragment in ('${MININGCORE_MIGRATION_PLAN:-}', 'none) return 0',
                         'elif [ "${MININGCORE_VERSION:-}" != v0.4.0-rc.1 ]; then',
                         'test -r "$release_dir/scripts/release/check-share-owner.sql" || return',
                         'test -r "$release_dir/scripts/release/check-pps-arithmetic-ownership.sql" || return',
                         'pps-arithmetic)', 'cumulative)', '*) return 1',
                         'unset MININGCORE_MIGRATION_PLAN',
                         'report_upgrade_stop()', 'return 1\n}',
                         'Failed step: %s', 'Service: %s', 'Schema: %s',
                         'docs/troubleshooting.md#when-an-rc1-upgrade-stops',
                         '"$release_dir/scripts/release/check-pps-arithmetic-ownership.sql" &&',
                         '"$release_dir/scripts/release/check-share-owner.sql" &&',
                         '"$release_dir/migrations/add_pps_arithmetic_version.sql"',
                         'apply_selected_migrations &&'):
            require(fragment in upgrade_blocks[0], f'Missing guarded upgrade route: {fragment}')
    troubleshooting = documents['docs/troubleshooting.md']
    require('## When an RC.1 upgrade stops' in troubleshooting,
            'Missing operator upgrade-stop entry point')
    if len(upgrade_blocks) == 1:
        steps = set(re.findall(r'miningcore_upgrade_step=([a-z-]+)', upgrade_blocks[0]))
        require(bool(steps) and all(f'`{step}`' in troubleshooting for step in steps),
                'Every reported upgrade step must have troubleshooting guidance')
    for name in ('docs/releases.md', 'docs/upgrade-v0.4.0-rc.1.md', 'docs/database.md'):
        require('troubleshooting.md#when-an-rc1-upgrade-stops' in documents[name],
                f'{name}: missing operator recovery reference')
    for fragment in ('Do not then replay the upgrade migrations.',
                     'Never rerun `createdb.sql`.',
                     'published stable v0.3.0', 'version 0'):
        require(fragment in prose(documents['docs/database.md']),
                f'Missing new-install versus upgrade boundary: {fragment}')
    require('https://github.com/NINJAK1DD/miningcore/blob/dd86092d2616b5ad7023382e1bdd95b59bfa548a/docs/releases.md#upgrade-or-roll-back' in release,
            'The stable v0.3.0 upgrade route must link to its immutable release runbook')
    require('only for v0.4.0-rc.1 archives' in prose(release),
            'The guarded candidate upgrade must be explicitly scoped to RC.1 archives')
    cumulative = console_blocks(documents['docs/database.md'],
                                '"$MININGCORE_CANDIDATE_DIR/migrations/add_auxpow_block_idempotency.sql"')
    require(len(cumulative) == 1, 'Expected one manual cumulative migration command')
    if len(cumulative) == 1:
        require(len(re.findall(r'\bpsql(?=\s|$)', cumulative[0])) ==
                cumulative[0].count('psql -X -v ON_ERROR_STOP=1') == 4,
                'All four manual cumulative SQL commands must fail closed and ignore psql startup files')
        for filename in ('scripts/release/check-share-owner.sql',
                         'migrations/add_auxpow_block_idempotency.sql',
                         'migrations/add_payout_manager_ownership.sql'):
            require(f'-f "$MININGCORE_CANDIDATE_DIR/{filename}" &&' in cumulative[0],
                    f'Manual cumulative migration must fail closed after {filename}')
        require('check-share-owner.sql' in cumulative[0] and
                cumulative[0].index('check-share-owner.sql') < cumulative[0].index('add_auxpow_block_idempotency.sql'),
                'Manual cumulative ownership preflight must precede migrations')
    extraction = console_blocks(upgrade, 'extract_candidate_sql()')
    require(len(extraction) == 1, 'Expected one stopped-container SQL extraction example')
    if len(extraction) == 1:
        for fragment in ('docker create --network none "$image"',
                         'docker rm -v "$container"', '"$container:/app/migrations"',
                         '"$container:/app/BUILD-INFO"', '"$container:/app/scripts/release"', 'rc=$?',
                         'trap cleanup_candidate EXIT', 'exit "$rc"'):
            require(fragment in extraction[0], f'Unsafe SQL extraction example: missing {fragment}')
        require('Candidate extraction directory (may be incomplete):' in extraction[0],
                'SQL extraction must identify partial-copy output')
    require('The function calls `docker` directly:' in prose(upgrade),
            'SQL extraction must explain Docker access requirements')
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
