#!/usr/bin/env python3
"""Shared offline cache and diagnostic contract for both pinned Knots gates."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

HISTORICAL_SCRIPTS = {
    'mempool_long_coinbase_maturity': 'ecef521550daa248222661bf90f2675dee2be3001aa8643e879f18aefb4e7fe3',
    'feature_chainstate_revalidation': '73c55c47ea4f07e71cf6330c392f08e118845df81a612c78c1b80ee3041186be',
}
ROLLING_SCRIPTS = {
    **HISTORICAL_SCRIPTS,
    'feature_rolling_coinbase_maturity': '2ded8be84400f25c146f8d5a75c63878598c7e265846419bae92320fee961bcc',
    'feature_rolling_coinbase_upgrade': '8733ebe5ba89528b0dd3852846e1c7e664e41c573bf128cd15c30f9941468b25',
}

class KnotsGateTests(unittest.TestCase):
    def setUp(self):
        self.gate = Path(__file__).with_name(f'validate-knots-{self.release}-maturity.sh').resolve()
        self.archive = f'bitcoin-{self.release}.knots20260508.tar.gz'
        self.temporary = tempfile.TemporaryDirectory(prefix='knots-gate-selftest-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.cache = self.root / 'cache'
        self.cache.mkdir()
        self.logs = self.root / 'logs'
        self.calls = self.root / 'calls'
        self.env = dict(os.environ, PATH=f"{self.bin}:{os.environ['PATH']}",
                        MININGCORE_KNOTS_SOURCE_CACHE=str(self.cache),
                        MININGCORE_KNOTS_TEST_ARTIFACTS=str(self.logs), CALLS=str(self.calls))
        self.stub('bitcoind', f"echo 'Bitcoin Knots version v{self.release}.knots20260508'")
        self.stub('test_bitcoin', 'echo mainnet-unit-suite >> "$CALLS"')
        self.env['MININGCORE_TEST_BLAKE2B_BITCOIND'] = str(self.bin / 'bitcoind')
        self.stub('curl', 'echo curl >> "$CALLS"; exit 98')

    def stub(self, name, commands):
        path = self.bin / name
        path.write_text('#!/usr/bin/env bash\nset -euo pipefail\n' + commands + '\n')
        path.chmod(0o755)

    def run_gate(self):
        return subprocess.run(['bash', str(self.gate)], env=self.env, text=True,
                              capture_output=True, timeout=15)

    def simulated_source(self, failure=False, corrupt=False):
        # Stub processes, never production checksum constants. A separate live
        # gate verifies the signed-release archive and unmodified source tests.
        self.stub('curl', '''echo curl >> "$CALLS"
while (($#)); do
  if [[ "$1" == '-o' ]]; then printf 'source fixture' > "$2"; break; fi
  shift
done''')
        self.stub('sha256sum', '''input="$(cat)"
printf '%s\n' "$input" >> "$CALLS"
[[ "$1" == '--check' && "$2" == '--strict' ]]
''' + ('exit 43' if corrupt else 'exit 0'))
        self.stub('tar', '''echo tar >> "$CALLS"
while (($#)); do
  if [[ "$1" == '-C' ]]; then source_root="$2"; break; fi
  shift
done
mkdir -p "$source_root/test/functional"
''' + '\n'.join(f'touch "$source_root/test/functional/{name}.py"' for name in self.scripts))
        self.stub('python3', '''echo python3 >> "$CALLS"
for arg in "$@"; do
  if [[ "$arg" == --tmpdir=* ]]; then
    run="${arg#--tmpdir=}"
    mkdir -p "$run/node0"
    printf 'framework diagnostic marker\n' > "$run/test_framework.log"
    printf 'daemon diagnostic marker\n' > "$run/node0/debug.log"
  fi
done
''' + ('exit 42' if failure else 'exit 0'))

    def test_corrupt_cached_archive_is_rejected_before_extract_or_network(self):
        (self.cache / self.archive).write_text('untrusted cached data')
        result = self.run_gate()  # real sha256sum
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('FAILED', result.stdout)
        self.assertFalse(self.calls.exists())

    def test_cache_hit_rechecks_archive_and_every_script_without_network(self):
        self.simulated_source()
        (self.cache / self.archive).write_text('cached source fixture')
        result = self.run_gate()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.calls.read_text()
        self.assertNotIn('curl', calls)
        self.assertIn(self.source_sha, calls)
        for checksum in self.scripts.values():
            self.assertIn(checksum, calls)
        self.assertEqual(calls.count('python3'), len(self.scripts))
        self.assertEqual('mainnet-unit-suite' in calls, self.release == '29.4.3')
        self.assertFalse(any(self.logs.iterdir()))

    def test_cache_miss_verifies_then_publishes_archive(self):
        self.simulated_source()
        result = self.run_gate()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((self.cache / self.archive).exists())
        self.assertEqual(self.calls.read_text().count('curl'), 1)
        self.assertFalse(list(self.cache.glob('.knots-source.*')))

    def test_failed_download_checksum_never_publishes_cache(self):
        self.simulated_source(corrupt=True)
        result = self.run_gate()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.cache / self.archive).exists())
        self.assertFalse(list(self.cache.glob('.knots-source.*')))
        self.assertNotIn('tar', self.calls.read_text())

    def test_upstream_failure_retains_and_prints_framework_and_daemon_logs(self):
        self.simulated_source(failure=True)
        result = self.run_gate()
        self.assertEqual(result.returncode, 42)
        self.assertIn('framework diagnostic marker', result.stderr)
        self.assertIn('daemon diagnostic marker', result.stderr)
        self.assertEqual(len(list(self.logs.rglob('test_framework.log'))), 1)
        self.assertEqual(len(list(self.logs.rglob('debug.log'))), 1)
        self.assertTrue((self.cache / self.archive).exists())
