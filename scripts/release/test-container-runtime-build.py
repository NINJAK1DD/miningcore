#!/usr/bin/env python3
"""Offline failure-propagation contracts for container dependencies and probes."""
import json
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('verify_probes', SCRIPTS / 'verify-container-probes.py')
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)


class ProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.dumps({'schema': 1, 'complete': True, 'application_sha256': 'a' * 64,
                                    'native_libraries': {'libfixture.so': 'b' * 64}})
        self.checksums = 'a' * 64 + '  Miningcore.dll\n' + 'b' * 64 + '  ./libfixture.so\n'

    def test_equal_image_is_accepted(self):
        verifier.verify(self.manifest, self.checksums)

    def test_different_image_is_rejected(self):
        with self.assertRaises(ValueError):
            verifier.verify(self.manifest, self.checksums.replace('b' * 64, 'c' * 64))

    def test_missing_native_is_rejected(self):
        with self.assertRaises(ValueError):
            verifier.verify(self.manifest, self.checksums.splitlines()[0])

    def test_extra_native_is_rejected(self):
        with self.assertRaises(ValueError):
            verifier.verify(self.manifest, self.checksums + 'c' * 64 + '  libextra.so\n')

    def test_duplicate_digest_is_rejected(self):
        with self.assertRaises(ValueError):
            verifier.verify(self.manifest, self.checksums + self.checksums)

    def test_incomplete_evidence_is_rejected(self):
        with self.assertRaises(ValueError):
            verifier.verify(self.manifest.replace('true', 'false'), self.checksums)

    def test_oversized_input_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            manifest = Path(temporary) / 'manifest.json'
            manifest.write_text(self.manifest)
            result = subprocess.run(['python3', str(SCRIPTS / 'verify-container-probes.py'), str(manifest)],
                                    input=' ' * 16385, capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)


class ContainerBuildTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='container-build-contract-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.trace = self.root / 'trace'
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
                        TEST_ROOT=str(self.root), TEST_TRACE=str(self.trace))

    def executable(self, name, source):
        path = self.bin / name
        path.write_text(source, encoding='utf-8')
        path.chmod(0o755)

    def apt(self, packages=None, **environment):
        self.executable('apt-get', '''#!/usr/bin/env bash
set -eu
printf '%s\\n' "$*" >> "$TEST_TRACE"
if [[ " $* " == *' update '* ]]; then
  if [[ ${STALL_UPDATE:-0} == 1 ]]; then sleep 20; fi
  exit "${UPDATE_STATUS:-0}"
fi
exit "${INSTALL_STATUS:-0}"
''')
        return subprocess.run(['bash', str(SCRIPTS / 'container-apt-install.sh'), *(packages if packages is not None else ['libssl-dev'])],
                              env=dict(self.env, **environment), capture_output=True, text=True, timeout=15)

    def test_update_failure_never_installs(self):
        result = self.apt(UPDATE_STATUS='23')
        self.assertEqual(23, result.returncode)
        self.assertNotIn(' install ', self.trace.read_text())

    def test_install_failure_is_preserved(self):
        self.assertEqual(37, self.apt(INSTALL_STATUS='37').returncode)

    def test_total_budget_stops_stalled_update(self):
        result = self.apt(STALL_UPDATE='1', MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS='1')
        self.assertEqual(124, result.returncode)
        self.assertNotIn(' install ', self.trace.read_text())
        self.assertIn('exceeded its bounded budget', result.stderr)

    def test_retries_and_request_deadlines_are_bounded(self):
        self.assertEqual(0, self.apt().returncode)
        commands = self.trace.read_text().splitlines()
        self.assertEqual(2, len(commands))
        self.assertIn('APT::Update::Error-Mode=any', commands[0])
        for command in commands:
            self.assertIn('Acquire::Retries=2', command)
            self.assertIn('Acquire::http::Timeout=30', command)
            self.assertIn('Acquire::https::Timeout=30', command)

    def test_invalid_budget_does_not_invoke_apt(self):
        self.assertEqual(64, self.apt(MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS='0').returncode)
        self.assertFalse(self.trace.exists())

    def test_malformed_packages_never_invoke_apt(self):
        for packages in ([], ['--allow-unauthenticated'], ['libssl-dev;true'], ['../package']):
            with self.subTest(packages=packages):
                self.assertEqual(64, self.apt(packages=packages).returncode)
                self.assertFalse(self.trace.exists())

    def test_builder_recommendations_are_preserved(self):
        self.assertEqual(0, self.apt(packages=['--install-recommends', 'clang']).returncode)
        self.assertIn(' --install-recommends ', self.trace.read_text())
        self.assertNotIn('--no-install-recommends', self.trace.read_text())

    def test_sigkill_is_not_labelled_definitely_a_timeout(self):
        result = self.apt(INSTALL_STATUS='137')
        self.assertEqual(137, result.returncode)
        self.assertIn('SIGKILL/OOM', result.stderr)
        self.assertNotIn('exceeded its bounded budget', result.stderr)

    def probes(self, **environment):
        scripts = self.root / 'scripts/release'
        scripts.mkdir(parents=True, exist_ok=True)
        for name in ('run-warning-audited-dotnet.sh', 'audit-source-build-warnings.sh', 'assert-warning-free-build.sh'):
            shutil.copyfile(SCRIPTS / name, scripts / name)
        app = self.root / 'build/Miningcore.dll'
        app.parent.mkdir(exist_ok=True)
        app.write_bytes(b'audited-application')
        (app.parent / 'libfixture.so').write_bytes(b'audited-native')
        built = self.root / 'src/Miningcore/bin/Release/net10.0/Miningcore.dll'
        built.parent.mkdir(parents=True, exist_ok=True)
        built.write_bytes(app.read_bytes())
        self.executable('dotnet', '''#!/usr/bin/env python3
import json, os
from pathlib import Path
import sys
args = sys.argv[1:]
with open(os.environ['TEST_TRACE'], 'a') as trace: trace.write(json.dumps(args) + '\\n')
if os.environ.get('COMMAND_DELAY'):
    import time
    time.sleep(float(os.environ['COMMAND_DELAY']))
if args[0] == os.environ.get('FAIL_COMMAND'): sys.exit(29)
if args[0] == os.environ.get('STALL_COMMAND'):
    import time
    time.sleep(20)
if args[0] == 'build' and '--no-dependencies' not in args: sys.exit(91)
for argument in args:
    if argument.startswith('-flp:LogFile='):
        log = Path(argument.split('=', 1)[1].split(';', 1)[0])
        log.write_text('fixture.cs(1): warning CS0001: fixture\\n' if os.environ.get('BUILD_WARNING') else 'Build succeeded\\n')
if args[0] == 'publish':
    output = Path(args[args.index('--output') + 1]); output.mkdir(parents=True)
    if 'ProcessHost' in args[1]:
        if '--no-build' not in args or '--no-restore' not in args: sys.exit(92)
        source = Path(os.environ['TEST_ROOT']) / 'build/Miningcore.dll'
        (output / 'Miningcore.dll').write_bytes(b'changed' if os.environ.get('CORRUPT_APPLICATION') else source.read_bytes())
        (output / 'Miningcore.Tests.ProcessHost.dll').write_bytes(b'fixture-host')
    else: (output / 'ZeroMqRuntimeProbe.dll').write_bytes(b'fixture-probe')
    if os.environ.get('NATIVE_PROBE') == ('host' if 'ProcessHost' in args[1] else 'zeromq'):
        native = output / 'nested/libinjected.so.1'; native.parent.mkdir()
        native.write_bytes(b'untrusted-native')
''')
        return subprocess.run(['python3', str(SCRIPTS / 'publish-container-probes.py'), str(self.root),
                               str(app.parent), str(self.root / 'probes')],
                              env=dict(self.env, **environment), capture_output=True, text=True, timeout=15)

    def test_probes_reuse_audited_application_and_record_fixed_evidence(self):
        result = self.probes()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        evidence = json.loads((self.root / 'probes/build-evidence.json').read_text())
        self.assertTrue(evidence['complete'])
        self.assertEqual(4, len(evidence['phases']))
        self.assertTrue(all(phase['status'] == 0 for phase in evidence['phases']))
        self.assertLess((self.root / 'probes/build-evidence.json').stat().st_size, 4096)
        self.assertNotIn('TEST_TRACE', json.dumps(evidence))

    def test_failed_restore_never_builds_or_publishes(self):
        self.assertEqual(29, self.probes(FAIL_COMMAND='restore').returncode)
        self.assertEqual(1, len(self.trace.read_text().splitlines()))

    def test_failed_build_never_publishes(self):
        self.assertEqual(29, self.probes(FAIL_COMMAND='build').returncode)
        self.assertEqual(2, len(self.trace.read_text().splitlines()))

    def test_failed_publish_does_not_continue(self):
        self.assertEqual(29, self.probes(FAIL_COMMAND='publish').returncode)
        self.assertEqual(3, len(self.trace.read_text().splitlines()))

    def test_probe_deadline_stops_stalled_restore(self):
        result = self.probes(STALL_COMMAND='restore', MININGCORE_PROBE_TIMEOUT_SECONDS='1')
        self.assertEqual(124, result.returncode)
        self.assertEqual(1, len(self.trace.read_text().splitlines()))

    def test_total_probe_budget_is_shared_across_phases(self):
        result = self.probes(COMMAND_DELAY='0.7', MININGCORE_PROBE_TIMEOUT_SECONDS='1')
        self.assertEqual(124, result.returncode)
        self.assertEqual(2, len(self.trace.read_text().splitlines()))

    def test_invalid_probe_budgets_never_invoke_dotnet(self):
        for budget in ('0', '-1', '601', 'invalid'):
            with self.subTest(budget=budget):
                self.assertNotEqual(0, self.probes(MININGCORE_PROBE_TIMEOUT_SECONDS=budget).returncode)
                self.assertFalse(self.trace.exists())

    def test_probe_natives_are_rejected(self):
        for target in ('host', 'zeromq'):
            with self.subTest(target=target):
                result = self.probes(NATIVE_PROBE=target)
                self.assertNotEqual(0, result.returncode)
                self.assertIn('must not contain native', result.stderr)
                shutil.rmtree(self.root / 'probes')

    def test_stale_export_directory_is_rejected(self):
        (self.root / 'probes').mkdir()
        self.assertNotEqual(0, self.probes().returncode)
        self.assertFalse(self.trace.exists())

    def test_warning_fails_the_probe_build(self):
        self.assertNotEqual(0, self.probes(BUILD_WARNING='1').returncode)
        self.assertEqual(2, len(self.trace.read_text().splitlines()))

    def test_warning_override_cannot_disable_probe_audit(self):
        self.assertNotEqual(0, self.probes(BUILD_WARNING='1', MININGCORE_ALLOW_BUILD_WARNINGS='1').returncode)

    def test_changed_application_is_rejected_before_zeromq_publish(self):
        self.assertNotEqual(0, self.probes(CORRUPT_APPLICATION='1').returncode)
        self.assertEqual(3, len(self.trace.read_text().splitlines()))


if __name__ == '__main__':
    unittest.main()
