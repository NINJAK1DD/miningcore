#!/usr/bin/env python3
"""Mutation tests for the release contracts; safe under python -O as well."""

import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest


SCRIPT = Path(__file__).with_name('check-release-preparation-docs.py')
SPEC = importlib.util.spec_from_file_location('release_contracts', SCRIPT)
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)


class ReleasePreparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.original = {name: (guard.ROOT / name).read_text(encoding='utf-8')
                        for name in guard.DOCUMENTS}
        cls.baseline = guard.BASELINE.read_text(encoding='utf-8')

    def mutation(self, name, old, new):
        documents = self.original.copy()
        self.assertIn(old, documents[name], 'mutation must exercise the intended text')
        documents[name] = documents[name].replace(old, new, 1)
        self.assertTrue(guard.validate(documents, self.baseline), 'mutation escaped the guard')

    def test_current_documents(self):
        self.assertEqual([], guard.validate(self.original, self.baseline))

    def test_future_unreleased_entry_allowed(self):
        documents = self.original.copy()
        documents['docs/releases.md'] += '\n## Unreleased (after v0.4.0-rc.1): future fix\n'
        self.assertEqual([], guard.validate(documents, self.baseline))

    def test_stale_or_unqualified_unreleased_entries_rejected(self):
        for heading in ('## Unreleased: future fix', '## Unreleased',
                        '## Unreleased (after v0.3.0): future fix',
                        '## Unreleased (after v0.4.0-rc.2): speculative fix'):
            with self.subTest(heading=heading):
                documents = self.original.copy()
                documents['docs/releases.md'] += '\n' + heading + '\n'
                self.assertTrue(guard.validate(documents, self.baseline))

    def test_unreleased_heading_level_and_case_cannot_bypass_guard(self):
        for level in ('#', '##', '###', '####', '#####', '######'):
            for title in ('unreleased: fix', 'UNRELEASED: fix', 'Unreleased: fix',
                          'Unreleased (after v0.4.0-rc.1): fix'):
                if level == '##' and title.startswith('Unreleased (after'):
                    continue
                with self.subTest(level=level, title=title):
                    documents = self.original.copy()
                    documents['docs/releases.md'] += f'\n{level} {title}\n'
                    self.assertTrue(guard.validate(documents, self.baseline))

    def test_fenced_unreleased_examples_do_not_become_release_entries(self):
        for marker in ('```', '~~~', '````'):
            with self.subTest(marker=marker):
                documents = self.original.copy()
                documents['docs/releases.md'] += f'\n{marker}markdown\n### unreleased: example\n{marker}\n'
                self.assertEqual([], guard.validate(documents, self.baseline))

    def test_setext_unreleased_entries_are_rejected(self):
        for title in ('Unreleased', 'unreleased: fix', 'UNRELEASED',
                      'Unreleased (after v0.4.0-rc.1): fix'):
            for underline in ('===', '---'):
                with self.subTest(title=title, underline=underline):
                    documents = self.original.copy()
                    documents['docs/releases.md'] += f'\n{title}\n{underline}\n'
                    self.assertTrue(guard.validate(documents, self.baseline))

    def test_setext_examples_in_code_are_ignored(self):
        for example in ('```markdown\nUnreleased\n---\n```',
                        '~~~markdown\nUnreleased\n===\n~~~',
                        '    Unreleased\n    ---'):
            with self.subTest(example=example):
                documents = self.original.copy()
                documents['docs/releases.md'] += '\n' + example + '\n'
                self.assertEqual([], guard.validate(documents, self.baseline))

    def test_exact_release_commit_is_required(self):
        self.mutation('docs/releases.md', 'git tag -s "$NEXT_VERSION" "$RELEASE_COMMIT" -m',
                      'git tag -s "$NEXT_VERSION" -m')

    def test_release_commit_reachability_is_required(self):
        self.mutation('docs/releases.md', 'git merge-base --is-ancestor "$RELEASE_COMMIT" origin/dev', 'true')

    def test_stable_version_flip_policy_is_required(self):
        self.mutation('docs/releases.md', 'For a stable release, put the version flip', 'Leave every version unchanged')

    def test_publication_wording_must_remain_true_after_release(self):
        self.mutation('docs/releases.md', 'Use wording that remains true before and after publication',
                      'Describe the current temporary branch state')

    def test_rejected_tag_target_must_not_report_success(self):
        self.mutation('docs/releases.md',
                      'echo "STOP: select the reviewed full commit SHA reachable from origin/dev" >&2\n  false',
                      'echo "STOP: select the reviewed full commit SHA reachable from origin/dev" >&2')

    def test_database_candidate_exception_is_required(self):
        self.mutation('docs/database.md', 'The cumulative example below deliberately targets v0.4.0-rc.1',
                      'This block applies to every release')

    def test_readme_links_directly_to_container_upgrade(self):
        self.mutation('README.md', 'docs/upgrade-v0.4.0-rc.1.md#container-upgrades',
                      'docs/upgrade-v0.4.0-rc.1.md')

    def test_sql_extraction_cleanup_is_required(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', 'docker rm -v "$container"', 'true')

    def test_sql_extraction_partial_directory_must_be_identified(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', 'Candidate extraction directory (may be incomplete):',
                      'Unspecified output directory:')

    def test_sql_extraction_access_requirements_must_be_explicit(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', 'The function calls `docker` directly:',
                      'Docker is available to every account:')

    def run_shell_example(self, name, needle, prefix, failure='', replacements=()):
        blocks = guard.console_blocks(self.original[name], needle)
        self.assertEqual(1, len(blocks))
        block = textwrap.dedent(blocks[0])
        for old, new in replacements:
            self.assertIn(old, block)
            block = block.replace(old, new, 1)
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / 'trace'
            env = dict(os.environ, TRACE=str(trace), FAIL=failure, TMPDIR=directory)
            result = subprocess.run(['bash', '-c', prefix + '\n' + block], env=env,
                                    capture_output=True, text=True, timeout=15)
            calls = trace.read_text().splitlines() if trace.exists() else []
        return result, calls

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_tagging_example_uses_reviewed_commit_and_fails_closed(self):
        prefix = r'''
git() {
  printf '%s\n' "$*" >> "$TRACE"
  [[ "$FAIL" != "$1" ]] || return 1
  case "$1" in
    rev-parse) printf '%s\n' "$RELEASE_COMMIT" ;;
    fetch|merge-base|show|tag|push) return 0 ;;
    *) return 95 ;;
  esac
}
'''
        sha = 'a' * 40
        for failure in ('', 'placeholder', 'fetch', 'rev-parse', 'merge-base', 'show', 'tag'):
            with self.subTest(failure=failure):
                replacements = () if failure == 'placeholder' else (('REPLACE_WITH_REVIEWED_MERGE_SHA', sha),)
                result, calls = self.run_shell_example('docs/releases.md', 'RELEASE_COMMIT=', prefix,
                                                       failure=failure, replacements=replacements)
                if not failure:
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn(f'tag -s v0.4.0-rc.1 {sha} -m Miningcore v0.4.0-rc.1', calls)
                    self.assertIn('push origin v0.4.0-rc.1', calls)
                else:
                    self.assertNotEqual(0, result.returncode, result.stderr)
                    self.assertFalse(any(call.startswith('push ') for call in calls), calls)
                    if failure != 'tag':
                        self.assertFalse(any(call.startswith('tag ') for call in calls), calls)

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_sql_extraction_example_never_starts_container_and_always_cleans_up(self):
        prefix = r'''
docker() {
  printf '%s\n' "$*" >> "$TRACE"
  case "$1" in
    create) [[ "$FAIL" != create ]] || return 1; printf '%s\n' fixture-container ;;
    cp) [[ "$FAIL" != "$2" ]] || return 1 ;;
    rm) return 0 ;;
    *) return 95 ;;
  esac
}
'''
        for failure in ('', 'create', 'fixture-container:/app/migrations', 'fixture-container:/app/BUILD-INFO'):
            with self.subTest(failure=failure):
                result, calls = self.run_shell_example('docs/upgrade-v0.4.0-rc.1.md',
                                                       'extract_candidate_sql()', prefix, failure=failure)
                self.assertTrue(calls[0].startswith('create --network none ghcr.io/ninjak1dd/miningcore@sha256:'))
                self.assertFalse(any(call.startswith(('start ', 'run ', 'exec ')) for call in calls), calls)
                if failure == 'create':
                    self.assertEqual(1, len(calls))
                    self.assertNotIn('Candidate extraction directory', result.stdout)
                else:
                    self.assertEqual('rm -v fixture-container', calls[-1])
                    self.assertIn('Candidate extraction directory (may be incomplete): ', result.stdout)
                if not failure:
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn('Inspect candidate SQL and BUILD-INFO', result.stdout)
                else:
                    self.assertNotEqual(0, result.returncode)
                    self.assertNotIn('Inspect candidate SQL and BUILD-INFO', result.stdout)

    def test_each_stable_boundary_is_required(self):
        for name in ('README.md', 'docs/releases.md'):
            # Keep original line wrapping so this mutation tests both documents.
            text = self.original[name]
            self.mutation(name, next(line for line in text.splitlines()
                                     if 'The latest stable baseline remains' in line), '')

    def test_installation_version_inventory(self):
        for name in ('README.md', 'docs/releases.md'):
            for bad in ('v0.4.0-rc.1', 'v0.2.1', 'v0.3.0-stale'):
                with self.subTest(name=name, bad=bad):
                    self.mutation(name, 'MININGCORE_VERSION=v0.3.0', f'MININGCORE_VERSION={bad}')
        self.mutation('docs/database.md', 'MININGCORE_VERSION=v0.4.0-rc.1', 'MININGCORE_VERSION=v0.3.0')

    def test_each_solo_row_is_protected_independently(self):
        for prefix in ('| Canonical BTC SOLO,', '| LTC/DOGE merged SOLO/SOLO,'):
            row = next(line for line in self.original['docs/upgrade-v0.4.0-rc.1.md'].splitlines()
                       if line.startswith(prefix))
            with self.subTest(prefix=prefix):
                self.mutation('docs/upgrade-v0.4.0-rc.1.md', row,
                              row.replace(guard.NO_SQL, 'Run add_share_accounting.sql.'))

    def test_legacy_anchor_deleted(self):
        self.mutation('docs/releases.md', '<a id="unreleased-after-v030-pearl-mining"></a>\n', '')

    def test_legacy_anchor_duplicated(self):
        alias = '<a id="unreleased-after-v030-pearl-mining"></a>'
        self.mutation('docs/releases.md', alias, alias + '\n' + alias)

    def test_coordinated_anchor_and_heading_rename_rejected(self):
        old = '<a id="unreleased-after-v030-pearl-mining"></a>\n\n## v0.4.0-rc.1: Pearl mining'
        self.mutation('docs/releases.md', old, old.replace('pearl-mining', 'new-mining').replace('Pearl mining', 'New mining'))

    def test_anchor_destination_changed(self):
        self.mutation('docs/releases.md', '## v0.4.0-rc.1: Pearl mining', '## v0.4.0-rc.1: Wrong destination')

    def test_nonmerged_exception_is_required(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', 'plain non-merged PROP/PPLNS/PPLNSBF', 'all pooled accounting')

    def test_container_migration_source_is_required(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', 'src/Miningcore/Persistence/Postgres/Scripts/', 'dev/migrations/')

    def test_published_container_migration_location_is_required(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', '`/app/migrations/` and `/app/BUILD-INFO`', 'no migration files')

    def test_automatic_migration_claim_is_rejected(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', 'Neither image automatically upgrades the database.',
                      'The entrypoint automatically upgrades the database.')

    def test_new_install_warning_is_required(self):
        self.mutation('docs/releases.md', 'new container installation only', 'any container installation')

    def test_bad_baseline_rejected(self):
        self.assertTrue(guard.validate(self.original, self.baseline.replace('unreleased-after-v030-pearl-mining', '')))


if __name__ == '__main__':
    unittest.main()
