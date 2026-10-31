#!/usr/bin/env python3
"""Exercise numeric PostgreSQL selection and bounded gate failure reporting."""
from pathlib import Path
import subprocess
import tempfile
import unittest

HELPERS = Path(__file__).with_name('knots-lab-helpers.sh').resolve()


class LabHelperTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='knots-lab-helper-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)

    def run_helper(self, command, *args):
        return subprocess.run(['bash', '-c', 'set -euo pipefail; source "$1"; ' + command,
                               'helper-test', str(HELPERS), *map(str, args)],
                              capture_output=True, text=True, timeout=10)

    def test_numeric_version_wins_over_lexically_later_9_6(self):
        for version in ['9.6', '18', '17.2']:
            binary = self.root / version / 'bin/initdb'
            binary.parent.mkdir(parents=True)
            binary.write_text('#!/bin/sh\nexit 0\n')
            binary.chmod(0o755)
        result = self.run_helper('knots_postgres_bin "$2"', self.root)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), str(self.root / '18/bin'))

    def test_missing_initdb_is_an_actionable_failure(self):
        result = self.run_helper('knots_postgres_bin "$2"', self.root)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('MININGCORE_TEST_POSTGRES_BIN', result.stderr)

    def test_gate_failure_keeps_full_log_and_prints_bounded_tail(self):
        gate = self.root / 'failed.sh'
        gate.write_text('for ((i=1;i<=300;i++)); do echo "diagnostic-$i"; done\nexit 42\n')
        log = self.root / 'gate.log'
        result = self.run_helper('knots_run_logged_gate "$2" "$3"', log, gate)
        self.assertEqual(result.returncode, 42)
        self.assertIn(str(log), result.stderr)
        self.assertIn('diagnostic-300', result.stderr)
        self.assertNotIn('diagnostic-1\n', result.stderr)
        self.assertEqual(len(log.read_text().splitlines()), 300)
        self.assertLessEqual(len(result.stderr.splitlines()), 201)

    def test_success_has_no_failure_diagnostics(self):
        gate = self.root / 'success.sh'
        gate.write_text('echo complete\n')
        log = self.root / 'gate.log'
        result = self.run_helper('knots_run_logged_gate "$2" "$3"', log, gate)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(log.read_text(), 'complete\n')
        self.assertEqual(result.stderr, '')


if __name__ == '__main__':
    unittest.main()
