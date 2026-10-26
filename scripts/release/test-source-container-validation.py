#!/usr/bin/env python3
"""Offline source attribution, interruption and cleanup regressions for the lab."""
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('validation_driver', SCRIPTS / 'validate-source-container.py')
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class ValidationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='source-lab-contract-')
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / 'repo'
        self.root.mkdir()
        (self.root / '.dockerignore').write_text((SCRIPTS.parents[1] / '.dockerignore').read_text())
        (self.root / '.gitignore').write_text('*.so\n__pycache__/\nbuild/\n')
        workflow = self.root / '.github/workflows/release.yml'
        workflow.parent.mkdir(parents=True)
        workflow.write_text('jobs:\n  source-container-check:\n    steps:\n'
                            '      - name: Test protected container listeners from host loopback\n        run: "true"\n')
        self.git('init', '-q')
        self.git('add', '.')
        self.git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture')

    def git(self, *arguments):
        return subprocess.check_output(['git', *arguments], cwd=self.root, stderr=subprocess.STDOUT)

    def test_clean_tree_and_docker_excluded_outputs_are_allowed(self):
        for path in ('build/evidence.json', 'src/sample/bin/output', 'scripts/__pycache__/module.pyc'):
            output = self.root / path
            output.parent.mkdir(parents=True, exist_ok=True)
            output.touch()
        self.assertEqual(self.git('rev-parse', 'HEAD').decode().strip(), driver.clean_source(self.root))

    def test_dirty_tracked_tree_is_rejected(self):
        (self.root / '.dockerignore').write_text('changed')
        with self.assertRaises(ValueError): driver.clean_source(self.root)

    def test_staged_tree_is_rejected(self):
        (self.root / '.gitignore').write_text('changed')
        self.git('add', '.gitignore')
        with self.assertRaises(ValueError): driver.clean_source(self.root)

    def test_untracked_docker_input_is_rejected_even_when_git_ignored(self):
        for name in ('untracked.txt', 'libstale.so'):
            with self.subTest(name=name):
                (self.root / name).touch()
                with self.assertRaises(ValueError): driver.clean_source(self.root)
                (self.root / name).unlink()

    def test_commit_change_during_validation_is_rejected(self):
        with self.assertRaises(ValueError): driver.clean_source(self.root, '0' * 40)

    def test_cleanup_failure_prevents_complete_success(self):
        evidence = {}
        self.assertEqual(1, driver.finish(evidence, True, 37, 0))
        self.assertTrue(evidence['functional_validation_passed'])
        self.assertFalse(evidence['complete'])

    def test_cleanup_preserves_original_failure(self):
        self.assertEqual(130, driver.finish({}, False, 37, 130))

    def test_cleanup_exception_and_status_are_reported(self):
        with patch.object(driver.subprocess, 'run', side_effect=subprocess.TimeoutExpired('docker', 60)):
            self.assertEqual('failed', driver.remove_builder('unique-fixture'))
        with patch.object(driver.subprocess, 'run', return_value=subprocess.CompletedProcess([], 37)):
            self.assertEqual(37, driver.remove_builder('unique-fixture'))

    def lab_process(self, **environment):
        binary = self.base / 'bin'
        binary.mkdir()
        docker = binary / 'docker'
        docker.write_text('''#!/usr/bin/env python3
import os, signal, sys, time
from pathlib import Path
args = sys.argv[1:]
with open(os.environ['DOCKER_TRACE'], 'a') as trace: trace.write(' '.join(args) + '\\n')
if args[:2] == ['buildx', 'build']:
    if os.environ.get('STALL_LAB'):
        Path(os.environ['LAB_MARKER']).write_text(str(os.getpid()))
        time.sleep(30)
    sys.exit(29)
if args[:2] == ['buildx', 'rm']: sys.exit(int(os.environ.get('CLEANUP_STATUS', '0')))
''')
        docker.chmod(0o755)
        output = self.root / 'build/validation'
        self.trace = self.base / 'trace'
        env = dict(os.environ, PATH=str(binary) + os.pathsep + os.environ['PATH'], DOCKER_TRACE=str(self.trace), **environment)
        process = subprocess.Popen(['python3', '-B', str(SCRIPTS / 'validate-source-container.py'),
                                    '--root', str(self.root), '--output', str(output)],
                                   env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        return process, output

    def test_failed_build_leaves_incomplete_evidence_and_removes_builder(self):
        process, output = self.lab_process(CLEANUP_STATUS='37')
        log, _ = process.communicate(timeout=10)
        self.assertNotEqual(0, process.returncode, log)
        evidence = json.loads((output / 'evidence.json').read_text())
        self.assertFalse(evidence['complete'])
        self.assertEqual(37, evidence['builder_cleanup_status'])
        self.assertIn('buildx rm issue223-', self.trace.read_text())

    def test_interruption_stops_child_and_cleans_up(self):
        marker = self.base / 'marker'
        process, output = self.lab_process(STALL_LAB='1', LAB_MARKER=str(marker))
        try:
            deadline = time.monotonic() + 5
            while not marker.exists() and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertTrue(marker.exists())
            process.send_signal(signal.SIGTERM)
            log, _ = process.communicate(timeout=15)
            self.assertEqual(130, process.returncode, log)
            evidence = json.loads((output / 'evidence.json').read_text())
            self.assertTrue(evidence['interrupted'])
            self.assertFalse(evidence['complete'])
            self.assertEqual(0, evidence['builder_cleanup_status'])
            with self.assertRaises(ProcessLookupError): os.kill(int(marker.read_text()), 0)
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()


if __name__ == '__main__':
    unittest.main()
