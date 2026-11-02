#!/usr/bin/env python3
"""Mutation tests for the release contracts; safe under python -O as well."""

import importlib.util
from pathlib import Path
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
