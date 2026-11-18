#!/usr/bin/env python3
"""Exercise production-content checks with clean and contaminated tar/image fixtures."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name('verify-production-contents.py')
spec = importlib.util.spec_from_file_location('production_contents', SCRIPT)
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


def tar_bytes(entries, compressed=False):
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode='w:gz' if compressed else 'w') as archive:
        for name, content in entries:
            info = tarfile.TarInfo(name)
            if isinstance(content, tuple):
                info.type, info.linkname = content
                archive.addfile(info)
            else:
                info.size = len(content)
                archive.addfile(info, io.BytesIO(content))
    return stream.getvalue()


class ProductionContentsTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.path = Path(self.scratch.name) / 'fixture.tar'

    def release(self, entries):
        self.path.write_bytes(tar_bytes([('release/Miningcore', b'host'), *entries], True))
        return policy.verify_archive(self.path)

    def image(self, layers, config=None):
        manifest = json.dumps([{'Config': 'config.json', 'Layers': [f'{n}/layer.tar' for n in range(len(layers))]}]).encode()
        entries = [('manifest.json', manifest), ('config.json', json.dumps(config or {}).encode())] + [(f'{n}/layer.tar', tar_bytes(layer)) for n, layer in enumerate(layers)]
        self.path.write_bytes(tar_bytes(entries))
        return policy.verify_saved_image(self.path)

    def test_clean_release_keeps_operator_material(self):
        self.release([(name, b'normal') for name in ['release/docs/database.md',
            'release/migrations/add_wallet_payout_control.sql', 'release/scripts/ops/audit.sql',
            'release/config.example.json', 'release/Miningcore.dll', 'release/Miningcore.pdb']])

    def test_synthetic_credentials_in_allowed_files_and_metadata_are_rejected(self):
        # Nonfunctional format-valid token, assembled so source scans do not
        # mistake the negative test itself for a published credential.
        canary = 'ghp_' + 'aB3dE6gH9jK2mN5pQ8sT1vW4yZ7bC0eF3hI6'
        for payload in (canary.encode(), canary.encode('utf-16-le'),
                        b'\x7fELF' + b'\0' * 65520 + canary.encode()):
            with self.subTest(encoding=len(payload)), self.assertRaisesRegex(ValueError, 'secret scan'):
                self.release([('release/settings.txt', payload)])
        for config in ({'config': {'Env': ['ACCESS_TOKEN=' + canary]}},
                       {'config': {'Labels': {'build.note': canary}}}):
            with self.subTest(config_type=list(config['config'])), self.assertRaisesRegex(ValueError, 'secret scan'):
                self.image([[('app/Miningcore', b'host')]], config)
        with self.assertRaisesRegex(ValueError, 'secret scan'):
            self.image([[('app/Miningcore', b'host'), ('app/settings.txt', canary.encode())],
                        [('app/.wh.settings.txt', b'')]])

    def test_binary_symbols_are_not_assignments_but_generic_credentials_are(self):
        value = 'aB3dE6gH9jK2mN5pQ8sT1vW4yZ7bC0eF3hI6'
        self.release([('release/Miningcore.pdb', ('linkedTokenSource>' + value + '\ncbPasswordDigest > ' + value + '\n').encode())])
        self.image([[('app/Miningcore', b'host'), ('etc/ssl/openssl.cnf', b'private_key = $dir/private/cakey.pem\n')]])
        with self.assertRaisesRegex(ValueError, 'secret scan'):
            self.release([('release/settings.txt', b'password = $dir/private/cakey.pem\n')])
        for name in ('API_KEY', 'password', 'rpcPassword', 'sharedEncryptionKey', 'client_secret'):
            for syntax in (name + '=' + value + ';', '"' + name + '": "' + value + '"'):
                with self.subTest(name=name), self.assertRaisesRegex(ValueError, 'secret scan'):
                    self.release([('release/settings.txt', syntax.encode())])

    def test_lab_and_generated_artifacts_are_rejected(self):
        for suffix in ['scripts/test-lab/README.md', './scripts/test-lab/run.py',
            'BUILD/TEST-LAB/result.json', 'TestResults/result.xml', 'result.trx',
            'Miningcore.Tests.ProcessHost.dll', 'client.keytab', 'client.ccache', 'wallet.dat',
            '.env.lab', 'logs/run.log', 'private.dump', 'source.bundle', '__pycache__/x.pyc',
            'wallet.sqlite', 'private.keys', 'config.json', 'pool.json', 'recovered-shares.txt',
            'build/ci/receipt.json', '.git/config', 'miningcore-gss-123/principal']:
            with self.subTest(suffix=suffix), self.assertRaises(ValueError):
                self.release([('release/'+suffix, b'private')])

    def test_links_cannot_hide_forbidden_targets(self):
        for kind in [tarfile.SYMTYPE, tarfile.LNKTYPE]:
            target = 'scripts/test-lab/key' if kind == tarfile.SYMTYPE else 'release/scripts/test-lab/key'
            with self.subTest(kind=kind), self.assertRaises(ValueError):
                self.release([('release/innocent', (kind, target))])

    def test_safe_relative_link_and_os_files_are_allowed(self):
        self.release([('release/libalias.so', (tarfile.SYMTYPE, 'libreal.so'))])
        self.image([[('app/Miningcore', b'host'), ('var/log/apt/history.log', b'os log'),
                     ('var/run', (tarfile.SYMTYPE, '/run'))]])

    def test_deleted_lower_layer_artifact_is_still_rejected(self):
        with self.assertRaises(ValueError):
            self.image([[('app/Miningcore', b'host'), ('app/scripts/test-lab/secret', b'private')],
                        [('app/scripts/.wh.test-lab', b'')]])

    def test_image_checks_artifacts_outside_app_and_link_targets(self):
        for entry in [('tmp/failed.trx', b'private'), ('opt/miningcore-test-lab/wallet', b'private'),
                      ('app/alias', (tarfile.SYMTYPE, '/tmp/client.keytab'))]:
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                self.image([[('app/Miningcore', b'host'), entry]])

    def test_unsafe_paths_and_escaping_links_fail(self):
        for name, content in [('../outside', b'x'), ('/absolute', b'x'), ('release\\bad', b'x'),
                              ('release/link', (tarfile.SYMTYPE, '../../escape'))]:
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.release([(name, content)])

    def test_empty_missing_or_malformed_inputs_fail(self):
        for data in [b'', b'not a tar', tar_bytes([], True)]:
            self.path.write_bytes(data)
            with self.subTest(data=data[:10]), self.assertRaises((ValueError, tarfile.TarError)):
                policy.verify_archive(self.path)
        for layers in [[], [[('etc/os-release', b'base image only')]]]:
            with self.subTest(layers=layers), self.assertRaises(ValueError):
                self.image(layers)

    def test_missing_layer_fails(self):
        self.path.write_bytes(tar_bytes([('manifest.json', b'[{"Layers":["missing.tar"]}]')]))
        with self.assertRaises(KeyError):
            policy.verify_saved_image(self.path)

    def test_zstd_layers_have_actionable_diagnostic(self):
        for magic in (0xFD2FB528, 0x184D2A50, 0x184D2A5F):
            self.path.write_bytes(tar_bytes([
                ('manifest.json', b'[{"Config":"config.json","Layers":["layer.tar"]}]'),
                ('config.json', b'{}'),
                ('layer.tar', magic.to_bytes(4, 'little') + b'fixture')]))
            with self.subTest(magic=magic), self.assertRaisesRegex(ValueError, 'Unsupported zstd.*uncompressed or gzip'):
                policy.verify_saved_image(self.path)

    def test_registry_requires_digest_before_any_docker_call(self):
        for reference in ('image:latest', 'image@sha256:short', '-option@sha256:' + 'a'*64,
                          'image@sha256:' + 'z'*64, 'image@sha256:' + 'a'*64 + '\n'):
            with self.subTest(reference=reference), patch.object(policy.subprocess, 'run') as docker:
                with self.assertRaisesRegex(ValueError, 'image reference|immutable'):
                    policy.verify_image(reference, pull=True)
                docker.assert_not_called()

    def test_registry_pull_failure_cannot_fall_back_to_preview(self):
        reference = 'example/image@sha256:' + 'a'*64
        with patch.object(policy.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'docker')) as docker:
            with self.assertRaises(subprocess.CalledProcessError):
                policy.verify_image(reference, pull=True)
            self.assertEqual(1, docker.call_count)
            self.assertEqual(['docker', 'pull', '--platform', 'linux/amd64', reference], docker.call_args.args[0])

    def test_registry_inspects_actual_digest_even_when_preview_was_clean(self):
        self.image([[('app/Miningcore', b'clean preview')]])
        preview = self.path.read_bytes()
        reference = 'example/image@sha256:' + 'b'*64
        for contaminated in (False, True):
            self.image([[('app/Miningcore', b'different registry build')]])
            if contaminated:
                # Simulate an independent second build with a deleted leak.
                with self.assertRaises(ValueError):
                    self.image([[('app/Miningcore', b'different registry build'), ('tmp/leak.trx', b'private')],
                                [('tmp/.wh.leak.trx', b'')]])
            registry = self.path.read_bytes()
            self.assertNotEqual(preview, registry)
            calls = []

            def docker(command, **kwargs):
                calls.append(command)
                self.assertTrue(kwargs['check'])
                self.assertEqual(600, kwargs['timeout'])
                if command[:3] == ['docker', 'image', 'save']:
                    Path(command[4]).write_bytes(registry)

            with self.subTest(contaminated=contaminated), patch.object(policy.subprocess, 'run', side_effect=docker):
                if contaminated:
                    with self.assertRaisesRegex(ValueError, 'Forbidden production artifact'):
                        policy.verify_image(reference, pull=True)
                else:
                    self.assertEqual((1, 1), policy.verify_image(reference, pull=True))
            self.assertEqual(['docker', 'pull', '--platform', 'linux/amd64', reference], calls[0])
            self.assertEqual(['docker', 'image', 'save', '--output'], calls[1][:4])
            self.assertEqual(reference, calls[1][-1])
            self.assertFalse(Path(calls[1][4]).exists(), 'Temporary export must be removed on success and failure')

    def test_image_export_failure_is_not_a_pass(self):
        with patch.object(sys, 'argv', [str(SCRIPT), '--image', 'fixture:local']), \
             patch.object(policy.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'docker')), \
             patch.object(sys, 'stderr', io.StringIO()), self.assertRaises(SystemExit) as failure:
            policy.main()
        self.assertEqual(1, failure.exception.code)

    def test_cli_fails_without_input_or_on_contamination(self):
        self.path.write_bytes(tar_bytes([('release/Miningcore', b'host'), ('release/leak.trx', b'x')], True))
        for args in [[], ['--archive', str(self.path)], ['--archive', str(self.path)+'.missing']]:
            result = subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
        self.release([])
        result = subprocess.run([sys.executable, str(SCRIPT), '--archive', str(self.path)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == '__main__':
    policy.secrets.install()
    unittest.main()
