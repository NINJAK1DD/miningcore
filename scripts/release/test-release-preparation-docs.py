#!/usr/bin/env python3
"""Mutation tests for the release contracts; safe under python -O as well."""

import importlib.util
import os
from pathlib import Path
import shutil
import shlex
import subprocess
import tempfile
import textwrap
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name('check-release-preparation-docs.py')
SPEC = importlib.util.spec_from_file_location('release_contracts', SCRIPT)
guard = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(guard)


def shell_matrix_errors(environ, find_shell, platform):
    mode = environ.get('MININGCORE_REQUIRE_SHELL_MATRIX')
    if mode in (None, '', '0'):
        return []
    if mode != '1':
        return ['MININGCORE_REQUIRE_SHELL_MATRIX must be 0 or 1']
    if platform != 'posix':
        return ['The required shell matrix needs a POSIX host']
    return [f'Required shell matrix is missing {shell}'
            for shell in ('bash', 'dash', 'zsh') if not find_shell(shell)]


class ReleasePreparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        errors = shell_matrix_errors(os.environ, shutil.which, os.name)
        if errors:
            raise RuntimeError('; '.join(errors))
        if os.environ.get('MININGCORE_REQUIRE_SHELL_MATRIX') == '1':
            print('Required shell matrix available: bash, dash, zsh')
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

    def test_shell_matrix_preflight(self):
        for shell in ('bash', 'dash', 'zsh'):
            with self.subTest(missing=shell):
                find_shell = lambda name: None if name == shell else f'/bin/{name}'
                self.assertEqual([f'Required shell matrix is missing {shell}'],
                                 shell_matrix_errors({'MININGCORE_REQUIRE_SHELL_MATRIX': '1'},
                                                     find_shell, 'posix'))
                self.assertEqual([], shell_matrix_errors({}, find_shell, 'posix'))
                # Exercise the actual unittest setup gate, not only the pure helper.
                with patch.dict(os.environ, {'MININGCORE_REQUIRE_SHELL_MATRIX': '1'}), \
                        patch.object(shutil, 'which', find_shell), patch.object(os, 'name', 'posix'):
                    with self.assertRaisesRegex(RuntimeError, f'missing {shell}'):
                        type(self).setUpClass()
        self.assertEqual([], shell_matrix_errors({'MININGCORE_REQUIRE_SHELL_MATRIX': '1'},
                                                 lambda name: f'/bin/{name}', 'posix'))
        self.assertTrue(shell_matrix_errors({'MININGCORE_REQUIRE_SHELL_MATRIX': '1'},
                                            lambda name: f'/bin/{name}', 'nt'))
        self.assertTrue(shell_matrix_errors({'MININGCORE_REQUIRE_SHELL_MATRIX': 'true'},
                                            lambda name: f'/bin/{name}', 'posix'))

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

    def test_tag_push_retry_requires_recorded_target_verification(self):
        self.mutation('docs/releases.md',
                      '`git rev-parse "${NEXT_VERSION}^{commit}"` exactly equals the',
                      'the current branch tip matches the')

    def test_tag_push_retry_must_not_recreate_or_force_push_tags(self):
        self.mutation('docs/releases.md', 're-create, move or force-push an existing local or remote release tag.',
                      'replace any existing tag to retry publication.')

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

    def run_shell_example(self, name, needle, prefix, failure='', replacements=(), shell='bash', observe_state=False, inspect=None):
        blocks = guard.console_blocks(self.original[name], needle)
        self.assertEqual(1, len(blocks))
        block = textwrap.dedent(blocks[0])
        for old, new in replacements:
            self.assertIn(old, block)
            block = block.replace(old, new, 1)
        with tempfile.TemporaryDirectory() as directory:
            trace = Path(directory) / 'trace'
            env = dict(os.environ, TRACE=str(trace), FAIL=failure, TMPDIR=directory)
            command = [shell, '-f', '-c'] if shell == 'zsh' else [shell, '-c']
            if observe_state:
                block += '\nrc=$?\nprintf "UPGRADE_STATE=%s;PLAN=%s\\n" "${MININGCORE_UPGRADE_READY:-}" "${MININGCORE_MIGRATION_PLAN-unset}"\nexit "$rc"\n'
            result = subprocess.run(command + [prefix + '\n' + block], env=env,
                                    capture_output=True, text=True, timeout=15)
            calls = trace.read_text().splitlines() if trace.exists() else []
            if inspect is not None:
                inspect(Path(directory), result, calls)
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
        for failure in ('', 'placeholder', 'fetch', 'rev-parse', 'merge-base', 'show', 'tag', 'push'):
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
                    if failure != 'push':
                        self.assertFalse(any(call.startswith('push ') for call in calls), calls)
                    else:
                        self.assertEqual(1, sum(call.startswith('tag ') for call in calls))
                    if failure not in ('tag', 'push'):
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
        for failure in ('', 'create', 'fixture-container:/app/migrations',
                        'fixture-container:/app/BUILD-INFO', 'fixture-container:/app/scripts/release'):
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
                    self.assertEqual(3, sum(call.startswith('cp ') for call in calls))
                    self.assertTrue(any('fixture-container:/app/scripts/release ' in call for call in calls))
                    self.assertFalse(any('fixture-container:/app/scripts ' in call or '/app/scripts/ops' in call for call in calls))
                    self.assertIn('Inspect candidate SQL and BUILD-INFO', result.stdout)
                else:
                    self.assertNotEqual(0, result.returncode)
                    self.assertNotIn('Inspect candidate SQL and BUILD-INFO', result.stdout)

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_sql_cleanup_failure_is_reported_without_masking_copy_failure(self):
        self.check_sql_cleanup_statuses('bash')

    @unittest.skipIf(os.name == 'nt', 'Execute shell examples in the Linux documentation suite')
    def test_sql_cleanup_statuses_under_dash(self):
        if not shutil.which('dash'):
            self.assertNotEqual('1', os.environ.get('MININGCORE_REQUIRE_SHELL_MATRIX'), 'Required dash disappeared')
            self.skipTest('dash is not installed')
        self.check_sql_cleanup_statuses('dash')

    @unittest.skipIf(os.name == 'nt', 'Execute shell examples in the Linux documentation suite')
    def test_sql_cleanup_statuses_under_zsh(self):
        if not shutil.which('zsh'):
            self.assertNotEqual('1', os.environ.get('MININGCORE_REQUIRE_SHELL_MATRIX'), 'Required zsh disappeared')
            self.skipTest('zsh is not installed')
        self.check_sql_cleanup_statuses('zsh')

    def test_sql_cleanup_does_not_use_zsh_readonly_status_variable(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md', 'rc=$?', 'status=$?')

    def check_sql_cleanup_statuses(self, shell):
        # Use POSIX syntax in the mock as well as the extraction example, so a
        # shell-specific mock failure cannot conceal a portability regression.
        prefix = r'''
docker() {
  printf '%s\n' "$*" >> "$TRACE"
  case "$1" in
    create)
      if [ "$FAIL" = create ]; then return 7; fi
      printf '%s\n' fixture-container ;;
    cp)
      if [ "$FAIL" = first-copy ] && [ "$2" = fixture-container:/app/migrations ]; then return 19; fi
      if [ "$FAIL" = audits-copy ] && [ "$2" = fixture-container:/app/scripts/release ]; then return 29; fi
      if [ "$FAIL" = copy ] || [ "$FAIL" = both ]; then
        if [ "$2" = fixture-container:/app/BUILD-INFO ]; then return 23; fi
      fi ;;
    rm)
      if [ "$FAIL" = cleanup ] || [ "$FAIL" = both ]; then return 17; fi ;;
    *) return 95 ;;
  esac
}
'''
        for failure, expected_status in (('', 0), ('create', 7), ('first-copy', 19),
                                         ('cleanup', 1), ('copy', 23), ('audits-copy', 29), ('both', 23)):
            with self.subTest(shell=shell, failure=failure):
                result, calls = self.run_shell_example('docs/upgrade-v0.4.0-rc.1.md',
                                                       'extract_candidate_sql()', prefix, failure=failure, shell=shell)
                self.assertEqual(expected_status, result.returncode, result.stderr)
                self.assertFalse(any(call.startswith(('start ', 'run ', 'exec ')) for call in calls), calls)
                if failure == 'create':
                    self.assertEqual(1, len(calls))
                    continue
                self.assertEqual('rm -v fixture-container', calls[-1])
                self.assertEqual(1, calls.count('rm -v fixture-container'))
                if failure in ('cleanup', 'both'):
                    self.assertIn('ERROR: Failed to remove temporary container fixture-container', result.stderr)
                else:
                    self.assertNotIn('ERROR: Failed to remove temporary container', result.stderr)
                if failure in ('first-copy', 'copy', 'audits-copy', 'both'):
                    self.assertNotIn('Inspect candidate SQL and BUILD-INFO', result.stdout)
                else:
                    self.assertIn('Inspect candidate SQL and BUILD-INFO', result.stdout)

    def test_container_audits_must_be_extracted(self):
        self.mutation('docs/upgrade-v0.4.0-rc.1.md',
                      'docker cp "$container:/app/scripts/release" "$output/scripts/" &&', 'true &&')

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_archive_upgrade_requires_explicit_plan_and_preserves_selected_sql_scope(self):
        prefix = self.upgrade_fixture() + '\nMININGCORE_MIGRATION_PLAN=$FAIL\nFAIL=\n'
        replacements = self.upgrade_replacements()
        for plan, expected in (('none', []), ('pps-arithmetic', ['check-pps-arithmetic-ownership.sql', 'add_pps_arithmetic_version.sql']),
                               ('cumulative', ['check-share-owner.sql', 'add_auxpow_block_idempotency.sql',
                                               'add_payout_manager_ownership.sql', 'add_share_accounting.sql']),
                               ('', None), ('NONE', None), ('unknown', None)):
            with self.subTest(plan=plan):
                result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                                                       prefix, failure=plan, replacements=replacements, observe_state=True)
                if expected is None:
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn('UPGRADE_STATE=;PLAN=unset', result.stdout)
                    self.assertIn('STOP: explicitly select', result.stderr)
                    self.assertIn('Failed step: migration-plan', result.stderr)
                    self.assertIn('Service: no stop requested by this block', result.stderr)
                    self.assertIn('Schema: no schema-changing SQL attempted by this block', result.stderr)
                    self.assertEqual([], calls, 'invalid plans must stop before staging or database access')
                    continue
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertIn('UPGRADE_STATE=1;PLAN=unset', result.stdout)
                self.assertIn(f'READY: completed {plan} plan', result.stdout)
                self.assertIn('Service remains stopped.', result.stdout)
                sql = [call for call in calls if ' psql ' in call]
                self.assertEqual(expected, [call.rsplit('/', 1)[-1] for call in sql])
                self.assertTrue(all('psql -X -v ON_ERROR_STOP=1' in call for call in sql))
                stop = next(i for i, call in enumerate(calls) if 'systemctl stop' in call)
                backup = next(i for i, call in enumerate(calls) if 'pg_dump' in call)
                restore = next(i for i, call in enumerate(calls) if call.startswith('restore '))
                self.assertLess(stop, backup)
                self.assertLess(backup, restore)
                self.assertTrue(all(calls.index(call) > restore for call in sql))
                self.assertTrue(calls[-1].startswith('sudo ln -sfnT '))

    @staticmethod
    def upgrade_replacements():
        return (('release_dir="/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}"',
                 'release_dir="$TMPDIR/candidate"'),
                ('$HOME/miningcore-before-', '${TMPDIR}/miningcore-before-'),
                ('LD_LIBRARY_PATH="$release_dir" "$release_dir/Miningcore" --version', 'fixture_version'))

    @staticmethod
    def upgrade_fixture():
        return r'''
MININGCORE_RELEASE_READY=1
MININGCORE_VERSION=v0.4.0-rc.1
MININGCORE_UBUNTU=26.04
MININGCORE_EXPECTED_SOURCE_COMMIT=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
archive=fixture.tar.gz
MININGCORE_UPGRADE_READY=stale
sudo() {
  printf 'sudo %s\n' "$*" >> "$TRACE"
  if [ "$1" = test ]; then [ "$FAIL" = existing-candidate ]; return; fi
  if [ -n "$FAIL" ]; then case "$*" in *"$FAIL"*) return 23 ;; esac; fi
  case "$*" in
    'tar -xzf '*)
      mkdir -p "$release_dir/migrations" "$release_dir/scripts/release"
      for file in add_pps_arithmetic_version.sql add_auxpow_block_idempotency.sql add_payout_manager_ownership.sql add_share_accounting.sql; do
        if [ "$FAIL" != "missing-selected:$file" ] && [ "$FAIL" != no-migration-files ]; then
          printf 'SELECT 1;\n' > "$release_dir/migrations/$file"
        fi
      done
      touch "$release_dir/scripts/release/check-share-owner.sql" "$release_dir/scripts/release/check-pps-arithmetic-ownership.sql"
      printf 'Version: %s\nSource commit: %s\nTarget: %s\n' \
        "${FIXTURE_BUILD_VERSION:-v0.4.0-rc.1}" "${FIXTURE_BUILD_SOURCE:-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa}" \
        "${FIXTURE_BUILD_TARGET:-Ubuntu 26.04 x64}" > "$release_dir/BUILD-INFO" ;;
    *pg_dump*) printf 'fixture backup\n' ;;
  esac
}
pg_restore() { printf 'restore %s\n' "$*" >> "$TRACE"; [ "$FAIL" != restore ]; }
cat() { printf 'identity %s\n' "$*" >> "$TRACE"; [ "$FAIL" != identity ] && command cat "$@"; }
fixture_version() {
  printf 'version\n' >> "$TRACE"
  [ "$FAIL" != version ] || return 1
  printf '%s [%s]\n' "${FIXTURE_RUNTIME_VERSION:-0.4.0-rc.1}" "${FIXTURE_RUNTIME_SOURCE:-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa}"
}
mktemp() { [ "$FAIL" != backup-location ] && command mktemp "$@"; }
test() {
  if [ "$1" = -d ]; then [ "$FAIL" != missing-migrations ]; return; fi
  if [ "$1" = -r ]; then
    case "$FAIL:$2" in
      missing-share-preflight:*/check-share-owner.sql|missing-arithmetic-preflight:*/check-pps-arithmetic-ownership.sql) return 1 ;;
    esac
    if [ "$FAIL" = "unreadable-selected:${2##*/}" ]; then return 1; fi
  fi
  command test "$@"
}
'''

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_archive_upgrade_never_activates_after_a_selected_step_fails(self):
        prefix = self.upgrade_fixture()
        replacements = self.upgrade_replacements()
        staging_failures = ('existing-candidate', 'mkdir', 'tar', 'missing-migrations',
                            'missing-share-preflight', 'missing-arithmetic-preflight', 'identity', 'version')
        for plan, failures in (('none', staging_failures + (
                                        'backup-location', 'systemctl stop', 'pg_dump', 'restore', 'ln -sfnT')),
                               ('pps-arithmetic', ('check-pps-arithmetic-ownership.sql', 'add_pps_arithmetic_version.sql', 'ln -sfnT')),
                               ('cumulative', ('check-share-owner.sql', 'add_auxpow_block_idempotency.sql',
                                               'add_payout_manager_ownership.sql', 'add_share_accounting.sql', 'ln -sfnT'))):
            for failure in failures:
                with self.subTest(plan=plan, failure=failure):
                    result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                        prefix + f'\nMININGCORE_MIGRATION_PLAN={plan}\n',
                        failure=failure, replacements=replacements, observe_state=True)
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn('STOP:', result.stderr)
                    steps = {'existing-candidate': 'candidate-directory', 'mkdir': 'staging-directory',
                             'tar': 'archive-extraction', 'missing-migrations': 'migration-files',
                             'missing-share-preflight': 'share-owner-file',
                             'missing-arithmetic-preflight': 'arithmetic-owner-file',
                             'identity': 'build-info', 'version': 'candidate-version',
                             'systemctl stop': 'service-stop', 'pg_dump': 'database-backup',
                             'restore': 'backup-inventory', 'check-pps-arithmetic-ownership.sql': 'arithmetic-owner-preflight',
                             'backup-location': 'backup-location',
                             'check-share-owner.sql': 'share-owner-preflight',
                             'add_pps_arithmetic_version.sql': 'arithmetic-migration',
                             'add_auxpow_block_idempotency.sql': 'auxpow-migration',
                             'add_payout_manager_ownership.sql': 'payout-ownership-migration',
                             'add_share_accounting.sql': 'accounting-migration', 'ln -sfnT': 'activation'}
                    self.assertIn(f'Failed step: {steps[failure]}\n', result.stderr)
                    self.assertIn('docs/troubleshooting.md#when-an-rc1-upgrade-stops', result.stderr)
                    service = ('no stop requested by this block' if failure in staging_failures or failure == 'backup-location' else
                               'stop requested; completion unconfirmed' if failure == 'systemctl stop' else
                               'stop confirmed by systemctl')
                    self.assertIn(f'Service: {service}\n', result.stderr)
                    migration_attempted = (failure.startswith('add_') or failure == 'ln -sfnT') and plan != 'none'
                    schema = ('migration SQL attempted; changes may have committed' if migration_attempted else
                              'no schema-changing SQL attempted by this block')
                    self.assertIn(f'Schema: {schema}\n', result.stderr)
                    self.assertIn('UPGRADE_STATE=;PLAN=unset', result.stdout)
                    self.assertNotIn('READY: completed', result.stdout)
                    if failure != 'ln -sfnT':
                        self.assertFalse(any('ln -sfnT' in call for call in calls), calls)
                    if failure in staging_failures:
                        self.assertFalse(any('systemctl stop' in call or 'pg_dump' in call or ' psql ' in call for call in calls), calls)
                    if failure == 'check-pps-arithmetic-ownership.sql':
                        self.assertFalse(any('add_pps_arithmetic_version.sql' in call or 'add_share_accounting.sql' in call for call in calls), calls)
                    if failure == 'check-share-owner.sql':
                        self.assertFalse(any('/migrations/' in call for call in calls), calls)

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_archive_rejection_is_nonzero_in_scripts_and_errexit_without_stale_state(self):
        for rejection, message in (('', 'no verified release archive'),
                                   ('MININGCORE_RELEASE_READY=1', 'explicitly select')):
            for errexit in ('', 'set -e\n'):
                with self.subTest(rejection=rejection, errexit=bool(errexit)):
                    prefix = errexit + 'unset MININGCORE_RELEASE_READY\nMININGCORE_VERSION=v0.4.0-rc.1\nMININGCORE_UPGRADE_READY=stale\nMININGCORE_MIGRATION_PLAN=invalid\n' + rejection
                    result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                                                           prefix, observe_state=True)
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn(message, result.stderr)
                    self.assertEqual([], calls)
                    if not errexit:
                        self.assertIn('UPGRADE_STATE=;PLAN=unset', result.stdout)

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_rc_archive_upgrade_rejects_stable_and_other_versions_before_any_action(self):
        for version in ('', 'v0.3.0', 'v0.2.1', 'v0.4.0', 'v0.4.0-rc.2', 'v0.4.0-rc.1-extra'):
            for plan in ('none', 'pps-arithmetic', 'cumulative'):
                for errexit in ('', 'set -e\n'):
                    with self.subTest(version=version, plan=plan, errexit=bool(errexit)):
                        prefix = errexit + f'''
MININGCORE_RELEASE_READY=1
MININGCORE_VERSION='{version}'
MININGCORE_MIGRATION_PLAN={plan}
MININGCORE_UPGRADE_READY=stale
sudo() {{ printf '%s\\n' "$*" >> "$TRACE"; return 99; }}
'''
                        result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                                                               prefix, observe_state=True)
                        self.assertNotEqual(0, result.returncode)
                        self.assertIn('only for v0.4.0-rc.1', result.stderr)
                        self.assertEqual([], calls, 'wrong releases must not stage, stop the service or access the database')
                        if not errexit:
                            self.assertIn('UPGRADE_STATE=;PLAN=unset', result.stdout)

    def test_stable_upgrade_route_and_rc_archive_gate_are_required(self):
        self.mutation('docs/releases.md',
                      'dd86092d2616b5ad7023382e1bdd95b59bfa548a/docs/releases.md#upgrade-or-roll-back',
                      'dev/docs/releases.md#upgrade-or-roll-back')
        self.mutation('docs/releases.md', 'only for v0.4.0-rc.1 archives', 'for every published archive')
        self.mutation('docs/releases.md', 'elif [ "${MININGCORE_VERSION:-}" != v0.4.0-rc.1 ]; then', 'elif false; then')
        for filename in ('check-share-owner.sql', 'check-pps-arithmetic-ownership.sql'):
            self.mutation('docs/releases.md', f'test -r "$release_dir/scripts/release/{filename}" || return', 'true')

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_upgrade_backups_preserve_existing_files_on_success_failure_and_collision(self):
        for failure in ('', 'pg_dump', 'backup-collision'):
            with self.subTest(failure=failure):
                prefix = self.upgrade_fixture() + r'''
MININGCORE_MIGRATION_PLAN=none
printf 'previous verified backup' > "$TMPDIR/miningcore-before-v0.4.0-rc.1.dump"
mkdir "$TMPDIR/prior-attempt"
printf 'previous attempt backup' > "$TMPDIR/prior-attempt/miningcore.dump"
if [ "$FAIL" = backup-collision ]; then
  mktemp() {
    reserved=$(command mktemp "$@") || return
    printf 'existing reserved backup' > "$reserved/miningcore.dump"
    printf '%s\n' "$reserved"
  }
fi
'''
                def inspect(directory, result, calls):
                    self.assertEqual(b'previous verified backup', (directory / 'miningcore-before-v0.4.0-rc.1.dump').read_bytes())
                    self.assertEqual(b'previous attempt backup', (directory / 'prior-attempt/miningcore.dump').read_bytes())
                    marker = 'Backup destination (may be incomplete): '
                    paths = [line[len(marker):] for line in result.stdout.splitlines() if line.startswith(marker)]
                    self.assertEqual(1, len(paths))
                    backup = Path(paths[0])
                    self.assertEqual(directory.resolve(), backup.parent.parent.resolve())
                    self.assertEqual(0o700, backup.parent.stat().st_mode & 0o777)
                    self.assertEqual(0o600, backup.stat().st_mode & 0o777)
                    if failure == 'backup-collision':
                        self.assertEqual(b'existing reserved backup', backup.read_bytes())
                        self.assertFalse(any('pg_dump' in call for call in calls))
                    elif not failure:
                        self.assertEqual(b'fixture backup\n', backup.read_bytes())
                result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                    prefix, failure=failure, replacements=self.upgrade_replacements(), inspect=inspect)
                self.assertEqual(bool(failure), result.returncode != 0, result.stderr)
                if failure:
                    self.assertIn('Failed step: database-backup', result.stderr)
                    self.assertFalse(any('ln -sfnT' in call or ' psql ' in call for call in calls))

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_selected_migrations_must_be_readable_before_shutdown(self):
        for plan, files in (('pps-arithmetic', ('add_pps_arithmetic_version.sql',)),
                            ('cumulative', ('add_auxpow_block_idempotency.sql',
                                            'add_payout_manager_ownership.sql', 'add_share_accounting.sql'))):
            for filename in files:
                for condition in ('missing', 'unreadable'):
                    with self.subTest(plan=plan, filename=filename, condition=condition):
                        result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                            self.upgrade_fixture() + f'\nMININGCORE_MIGRATION_PLAN={plan}\n',
                            failure=f'{condition}-selected:{filename}', replacements=self.upgrade_replacements(), observe_state=True)
                        self.assertNotEqual(0, result.returncode)
                        self.assertIn('Failed step: selected-migration-files', result.stderr)
                        self.assertIn('Service: no stop requested by this block', result.stderr)
                        self.assertIn('UPGRADE_STATE=;PLAN=unset', result.stdout)
                        self.assertFalse(any('systemctl stop' in call or 'pg_dump' in call or ' psql ' in call for call in calls))
        result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
            self.upgrade_fixture() + '\nMININGCORE_MIGRATION_PLAN=none\n', failure='no-migration-files',
            replacements=self.upgrade_replacements())
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(any(' psql ' in call for call in calls))

    @unittest.skipIf(os.name == 'nt', 'Execute Bash examples in the Linux documentation suite')
    def test_candidate_identity_must_match_independent_source_before_shutdown(self):
        a, b = 'a' * 40, 'b' * 40
        cases = (({'MININGCORE_EXPECTED_SOURCE_COMMIT': ''}, 'release-source'),
                 ({'MININGCORE_EXPECTED_SOURCE_COMMIT': 'short'}, 'release-source'),
                 ({'MININGCORE_UBUNTU': '24.04'}, 'release-target'),
                 ({'FIXTURE_BUILD_VERSION': 'v0.3.0'}, 'candidate-identity'),
                 ({'FIXTURE_BUILD_TARGET': 'Ubuntu 22.04 x64'}, 'candidate-identity'),
                 ({'FIXTURE_BUILD_SOURCE': b}, 'candidate-identity'),
                 ({'FIXTURE_BUILD_SOURCE': b, 'FIXTURE_RUNTIME_SOURCE': b}, 'candidate-identity'),
                 ({'FIXTURE_BUILD_SOURCE': a + '\nSource commit: ' + a}, 'candidate-identity'),
                 ({'FIXTURE_RUNTIME_VERSION': '0.3.0'}, 'candidate-version'),
                 ({'FIXTURE_RUNTIME_SOURCE': b}, 'candidate-version'))
        for values, step in cases:
            with self.subTest(values=values):
                setup = '\n'.join(f'{name}={shlex.quote(value)}' for name, value in values.items())
                result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                    self.upgrade_fixture() + '\nMININGCORE_MIGRATION_PLAN=none\n' + setup + '\n',
                    replacements=self.upgrade_replacements(), observe_state=True)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(f'Failed step: {step}', result.stderr)
                self.assertIn('UPGRADE_STATE=;PLAN=unset', result.stdout)
                self.assertFalse(any('systemctl stop' in call or 'pg_dump' in call or ' psql ' in call for call in calls))
                self.assertNotIn('Backup destination', result.stdout)
                if step in ('release-source', 'release-target'):
                    self.assertEqual([], calls)
        for ubuntu in ('22.04', '26.04'):
            setup = f'\nMININGCORE_MIGRATION_PLAN=pps-arithmetic\nMININGCORE_UBUNTU={ubuntu}\nFIXTURE_BUILD_TARGET="Ubuntu {ubuntu} x64"\n'
            result, _ = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=',
                self.upgrade_fixture() + setup, replacements=self.upgrade_replacements())
            self.assertEqual(0, result.returncode, result.stderr)

    def test_source_lookup_and_portable_validation_are_guarded(self):
        for old, new in (('git ls-remote --exit-code --tags', 'git ls-remote --tags'),
                         ('"refs/tags/${MININGCORE_VERSION}^{}"', '"refs/tags/${MININGCORE_VERSION}"'),
                         ('MININGCORE_EXPECTED_SOURCE_COMMIT=\n', ''),
                         ('This lookup trusts GitHub over TLS; it is not signature verification.', ''),
                         ('elif ! printf \'%s\\n\' "${MININGCORE_EXPECTED_SOURCE_COMMIT:-}" | grep -Eqx \'[0-9a-f]{40}([0-9a-f]{24})?\'; then',
                          'elif [[ ! "${MININGCORE_EXPECTED_SOURCE_COMMIT:-}" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ ]]; then')):
            with self.subTest(fragment=old):
                self.mutation('docs/releases.md', old, new)

    def test_source_lookup_sha_format_check_cannot_be_removed_or_weakened(self):
        check = 'printf \'%s\\n\' "$release_source_sha" | grep -Eqx \'[0-9a-f]{40}([0-9a-f]{24})?\'; then'
        for replacement in ('true; then', check.replace('grep -Eqx', 'grep -Eq'),
                            check.replace('[0-9a-f]{40}([0-9a-f]{24})?', '.+')):
            with self.subTest(replacement=replacement):
                self.mutation('docs/releases.md', check, replacement)

    @unittest.skipIf(os.name == 'nt', 'Execute shell examples in the Linux documentation suite')
    def test_release_source_lookup_fails_closed_across_shells(self):
        prefix = r'''
MININGCORE_VERSION=v0.4.0-rc.1
MININGCORE_EXPECTED_SOURCE_COMMIT=stale
git() {
  printf '%s\n' "$*" >> "$TRACE"
  ref="refs/tags/${MININGCORE_VERSION}^{}"
  sha=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
  case "$FAIL" in
    network) printf '%s\t%s\n' "$sha" "$ref"; return 23 ;;
    missing|lightweight) return 2 ;;
    empty) return 0 ;;
    wrong-ref) printf '%s\t%s\n' "$sha" "refs/tags/$MININGCORE_VERSION" ;;
    duplicate) printf '%s\t%s\n%s\t%s\n' "$sha" "$ref" "$sha" "$ref" ;;
    malformed) printf 'short\t%s\n' "$ref" ;;
    sha256) printf '%s%s\t%s\n' "$sha" aaaaaaaaaaaaaaaaaaaaaaaa "$ref" ;;
    *) printf '%s\t%s\n' "$sha" "$ref" ;;
  esac
}
'''
        for shell in ('bash', 'dash', 'zsh'):
            if not shutil.which(shell):
                self.assertNotEqual('1', os.environ.get('MININGCORE_REQUIRE_SHELL_MATRIX'))
                continue
            for failure in ('', 'sha256', 'network', 'missing', 'lightweight', 'empty', 'wrong-ref', 'duplicate', 'malformed'):
                for errexit in (False, True):
                    with self.subTest(shell=shell, failure=failure, errexit=errexit):
                        setup = ('set -e\n' if errexit else '') + prefix
                        result, calls = self.run_shell_example('docs/releases.md', 'release_source_ref=$(git ls-remote',
                            setup, failure=failure, shell=shell, observe_state=True)
                        self.assertEqual(['ls-remote --exit-code --tags https://github.com/NINJAK1DD/miningcore.git refs/tags/v0.4.0-rc.1^{}'], calls)
                        if failure in ('', 'sha256'):
                            self.assertEqual(0, result.returncode, result.stderr)
                            sha = 'a' * (64 if failure == 'sha256' else 40)
                            self.assertIn(f'Expected release source commit: {sha}\n', result.stdout)
                        else:
                            self.assertNotEqual(0, result.returncode)
                            self.assertIn('STOP: could not resolve', result.stderr)
                            self.assertNotIn('Expected release source commit:', result.stdout)
            # Inspect state after a failed lookup, without masking its status.
            result, _ = self.run_shell_example('docs/releases.md', 'release_source_ref=$(git ls-remote', prefix,
                failure='network', shell=shell,
                replacements=(("  false\nfi", "  test -z \"$MININGCORE_EXPECTED_SOURCE_COMMIT\" || exit 97\n  false\nfi"),))
            self.assertEqual(1, result.returncode, result.stderr)

    @unittest.skipIf(os.name == 'nt', 'Execute real Git fixtures in the Linux documentation suite')
    def test_source_lookup_resolves_real_annotated_tags_not_tag_objects(self):
        prefix = r'''
MININGCORE_VERSION=v0.4.0-rc.1
command git init -q --object-format="$FAIL" "$TMPDIR/repo" || exit
command git -C "$TMPDIR/repo" -c user.name=Fixture -c user.email=fixture@example.invalid \
  -c commit.gpgsign=false commit -q --allow-empty -m fixture || exit
command git -C "$TMPDIR/repo" -c user.name=Fixture -c user.email=fixture@example.invalid \
  -c tag.gpgsign=false tag -a "$MININGCORE_VERSION" -m fixture || exit
expected=$(command git -C "$TMPDIR/repo" rev-parse HEAD) || exit
tag_object=$(command git -C "$TMPDIR/repo" rev-parse "refs/tags/$MININGCORE_VERSION") || exit
test "$expected" != "$tag_object" || exit 98
printf 'Real fixture commit: %s\n' "$expected"
git() {
  printf '%s\n' "$*" >> "$TRACE"
  shift 4
  command git -C "$TMPDIR/repo" ls-remote --exit-code --tags "$TMPDIR/repo" "$@"
}
'''
        for object_format in ('sha1', 'sha256'):
            with self.subTest(object_format=object_format):
                result, calls = self.run_shell_example('docs/releases.md', 'release_source_ref=$(git ls-remote',
                    prefix, failure=object_format)
                self.assertEqual(0, result.returncode, result.stderr)
                expected = next(line.removeprefix('Real fixture commit: ') for line in result.stdout.splitlines()
                                if line.startswith('Real fixture commit: '))
                self.assertIn(f'Expected release source commit: {expected}\n', result.stdout)
                self.assertEqual(1, len(calls))

    @unittest.skipIf(os.name == 'nt', 'Execute shell examples in the Linux documentation suite')
    def test_archive_upgrade_source_validation_is_portable(self):
        for shell in ('bash', 'dash', 'zsh'):
            if not shutil.which(shell):
                self.assertNotEqual('1', os.environ.get('MININGCORE_REQUIRE_SHELL_MATRIX'))
                continue
            for sha in ('a' * 40, 'a' * 64, '', 'short', 'A' * 40):
                with self.subTest(shell=shell, sha=sha):
                    prefix = self.upgrade_fixture() + '\nMININGCORE_MIGRATION_PLAN=none\n'
                    prefix += f'MININGCORE_EXPECTED_SOURCE_COMMIT={shlex.quote(sha)}\n'
                    prefix += f'FIXTURE_BUILD_SOURCE={shlex.quote(sha)}\nFIXTURE_RUNTIME_SOURCE={shlex.quote(sha)}\n'
                    result, calls = self.run_shell_example('docs/releases.md', 'MININGCORE_UPGRADE_READY=', prefix,
                        shell=shell, replacements=self.upgrade_replacements())
                    if len(sha) in (40, 64) and sha.islower():
                        self.assertEqual(0, result.returncode, result.stderr)
                        self.assertTrue(any('ln -sfnT' in call for call in calls))
                    else:
                        self.assertNotEqual(0, result.returncode)
                        self.assertIn('Failed step: release-source', result.stderr)
                        self.assertEqual([], calls)

    def test_archive_plan_routes_and_migration_boundaries_are_required(self):
        for fragment in ('none) return 0', 'pps-arithmetic)', 'cumulative)',
                         'unset MININGCORE_MIGRATION_PLAN',
                         '"$release_dir/scripts/release/check-pps-arithmetic-ownership.sql" &&',
                         '"$release_dir/scripts/release/check-share-owner.sql" &&',
                         '"$release_dir/migrations/add_pps_arithmetic_version.sql"'):
            with self.subTest(fragment=fragment):
                self.mutation('docs/releases.md', fragment, 'BROKEN')
        for name in ('docs/database.md', 'docs/releases.md'):
            self.mutation(name, 'pps-arithmetic-migration.md#explicit-one-way-activation', 'database.md')

    def test_manual_standalone_commands_require_fail_closed_ownership_preflight(self):
        preflight = '-f "$MININGCORE_CANDIDATE_DIR/scripts/release/check-pps-arithmetic-ownership.sql" &&'
        for name in ('docs/database.md', 'docs/pps-arithmetic-migration.md'):
            for replacement in ('', preflight.replace(' &&', ''), 'true &&'):
                with self.subTest(document=name, replacement=replacement):
                    self.mutation(name, preflight, replacement)
            self.mutation(name, 'psql -X -v ON_ERROR_STOP=1', 'psql')
            documents = self.original.copy()
            blocks = guard.console_blocks(documents[name], '"$MININGCORE_CANDIDATE_DIR/migrations/add_pps_arithmetic_version.sql"')
            self.assertEqual(1, len(blocks))
            # Both filenames remaining somewhere in the page must not satisfy
            # the guard when the preflight no longer gates the migration.
            reordered = '\n'.join(reversed(blocks[0].splitlines()))
            documents[name] = documents[name].replace(blocks[0], reordered, 1)
            self.assertTrue(guard.validate(documents, self.baseline))

    def test_manual_cumulative_preflight_and_failure_chain_are_required(self):
        for filename in ('scripts/release/check-share-owner.sql',
                         'migrations/add_auxpow_block_idempotency.sql',
                         'migrations/add_payout_manager_ownership.sql'):
            line = f'-f "$MININGCORE_CANDIDATE_DIR/{filename}" &&'
            with self.subTest(filename=filename):
                self.mutation('docs/database.md', line, line.replace(' &&', ''))
        self.mutation('docs/database.md',
                      '-f "$MININGCORE_CANDIDATE_DIR/scripts/release/check-share-owner.sql" &&', 'true &&')

    def test_each_manual_cumulative_psql_call_requires_startup_isolation_and_error_stop(self):
        name = 'docs/database.md'
        blocks = guard.console_blocks(self.original[name],
            '"$MININGCORE_CANDIDATE_DIR/migrations/add_auxpow_block_idempotency.sql"')
        self.assertEqual(1, len(blocks))
        lines = blocks[0].splitlines()
        calls = [index for index, line in enumerate(lines) if line.startswith('sudo -u postgres psql ')]
        self.assertEqual(4, len(calls))
        for index in calls:
            for flag in (' -X', ' -v ON_ERROR_STOP=1'):
                with self.subTest(command=index, missing_flag=flag):
                    changed = lines.copy()
                    self.assertIn(flag, changed[index])
                    changed[index] = changed[index].replace(flag, '', 1)
                    documents = self.original.copy()
                    documents[name] = documents[name].replace(blocks[0], '\n'.join(changed) + '\n', 1)
                    self.assertTrue(guard.validate(documents, self.baseline),
                                    'each individual SQL command must retain its safety flags')

    def test_manual_migration_blocks_reject_extra_psql_invocations(self):
        cases = (('docs/database.md', 'add_pps_arithmetic_version.sql'),
                 ('docs/pps-arithmetic-migration.md', 'add_pps_arithmetic_version.sql'),
                 ('docs/database.md', 'add_auxpow_block_idempotency.sql'))
        extras = ("psql -d miningcore -c 'SELECT 1;'",
                  "psql\t-d miningcore -c 'SELECT 1;'",
                  'psql',
                  "sudo -u postgres psql -X -v ON_ERROR_STOP=1 -d miningcore -c 'SELECT 1;'")
        for name, filename in cases:
            blocks = guard.console_blocks(self.original[name],
                f'"$MININGCORE_CANDIDATE_DIR/migrations/{filename}"')
            self.assertEqual(1, len(blocks))
            for extra in extras:
                for position in ('before', 'after'):
                    with self.subTest(document=name, block=filename, extra=extra, position=position):
                        changed = extra + '\n' + blocks[0] if position == 'before' else blocks[0] + extra
                        documents = self.original.copy()
                        documents[name] = documents[name].replace(blocks[0], changed + '\n', 1)
                        self.assertTrue(guard.validate(documents, self.baseline),
                                        'additional SQL commands must not escape the reviewed invocation inventory')

    def test_operator_recovery_and_fresh_install_boundaries_are_required(self):
        self.mutation('docs/troubleshooting.md', '`activation`', '`unknown-stage`')
        for name in ('docs/releases.md', 'docs/upgrade-v0.4.0-rc.1.md', 'docs/database.md'):
            with self.subTest(document=name):
                documents = self.original.copy()
                documents[name] = documents[name].replace('troubleshooting.md#when-an-rc1-upgrade-stops', 'troubleshooting.md')
                self.assertTrue(guard.validate(documents, self.baseline))
        for fragment in ('Do not then replay the upgrade migrations.', 'Never rerun `createdb.sql`.'):
            self.mutation('docs/database.md', fragment, 'Always run the full schema again.')
        self.mutation('docs/releases.md', 'Failed step: %s', 'Something failed: %s')

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

    def test_historical_audit_scope_cannot_hide_missing_financial_evidence(self):
        for old in ('**The full audit is the default.**',
                    'Never automatically fall back to the shares-only script.',
                    'Their absence alone does not establish eligibility.',
                    'cannot clear missing PPS evidence.'):
            with self.subTest(boundary=old):
                self.mutation('docs/bitcoin-minimum-difficulty.md', old, 'Assume the missing history is clean.')
        self.mutation('docs/upgrade-v0.4.0-rc.1.md',
                      'a failed full audit must not trigger an automatic fallback.',
                      'ignore full-audit errors and use the legacy script.')

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
