#!/usr/bin/env python3
"""Exercise the discovery gate without editing real host registration.

The apphost fixture simulates success/failure; actual .NET discovery remains
covered by both Ubuntu source jobs using the published Miningcore executable.
"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name('test-ubuntu-apphost-discovery.sh')
OVERRIDES = ('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_HOST_PATH', 'DOTNET_ROLL_FORWARD')


class DiscoveryFixtures(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.registration = self.root / 'registration'
        self.registration.mkdir()
        self.publish = self.root / 'published app'
        self.publish.mkdir()
        self.generic = self.registration / 'install_location'
        self.arch = self.registration / 'install_location_x64'
        self.generic.write_text('/usr/lib/dotnet\n')
        self.arch.write_text('/usr/lib/dotnet\n')
        self.receipt = self.root / 'launched'
        app = self.publish / 'Miningcore'
        app.write_text('''#!/bin/bash
set -euo pipefail
test "$#" = 1 && test "$1" = --version
test "${DOTNET_ROOT+x}${DOTNET_ROOT_X64+x}${DOTNET_HOST_PATH+x}${DOTNET_ROLL_FORWARD+x}" = ""
test "$LD_LIBRARY_PATH" = "$EXPECTED_PUBLISH"
touch "$RECEIPT"
if [ "${FAIL_DISCOVERY:-0}" = 1 ]; then
  echo "Fixture: runtime discovery unresolved" >&2
  exit 150
fi
echo "Fixture: launched"
''')
        app.chmod(0o755)

    def invoke(self, fail_discovery=False):
        environment = dict(os.environ, **{key: '/wrong/override' for key in OVERRIDES})
        environment.update(EXPECTED_PUBLISH=str(self.publish), RECEIPT=str(self.receipt),
                           LD_LIBRARY_PATH='/wrong/libraries', FAIL_DISCOVERY=str(int(fail_discovery)))
        return subprocess.run(['bash', '-c', 'source "$1"; validate_apphost_discovery "$2" "$3"',
                               'fixture', str(SCRIPT), str(self.publish), str(self.registration)],
                              env=environment, text=True, capture_output=True, timeout=10)

    def test_registered_launch_clears_overrides(self):
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(self.receipt.exists())

    def test_generic_registration_without_optional_arch_file(self):
        self.arch.unlink()
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)

    def test_missing_generic_registration_rejects_before_launch(self):
        self.generic.unlink()
        self.assertNotEqual(0, self.invoke().returncode)
        self.assertFalse(self.receipt.exists())

    def test_wrong_registration_rejects_before_launch(self):
        for path in (self.generic, self.arch):
            for value in ('/missing/runtime\n', '/usr/lib/dotnet\n/another/root\n', ''):
                with self.subTest(file=path.name, value=value):
                    path.write_text(value)
                    result = self.invoke()
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn('Expected Canonical runtime registration', result.stderr)
                    self.assertFalse(self.receipt.exists())
                    path.write_text('/usr/lib/dotnet\n')

    def test_broken_arch_symlink_rejects_before_launch(self):
        self.arch.unlink()
        self.arch.symlink_to(self.root / 'absent')
        self.assertNotEqual(0, self.invoke().returncode)
        self.assertFalse(self.receipt.exists())

    def test_unresolved_discovery_failure_propagates(self):
        result = self.invoke(fail_discovery=True)
        self.assertEqual(150, result.returncode)
        self.assertIn('runtime discovery unresolved', result.stderr)
        self.assertTrue(self.receipt.exists())

    def test_cli_rejects_registration_override(self):
        result = subprocess.run(['bash', str(SCRIPT), str(self.publish), str(self.registration)],
                                text=True, capture_output=True, timeout=10)
        self.assertEqual(64, result.returncode)
        self.assertFalse(self.receipt.exists())


if __name__ == '__main__':
    unittest.main()
