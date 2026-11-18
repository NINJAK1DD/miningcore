#!/usr/bin/env python3
"""Prove no destination write precedes candidate inspection."""
import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('publisher', Path(__file__).with_name('publish-verified-image.py'))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)
DIGEST = 'sha256:' + 'a' * 64
SOURCE = '127.0.0.1:5000/miningcore@' + DIGEST
DESTINATION = 'docker.io/example/miningcore:latest'


class PublicationTests(unittest.TestCase):
    def test_attestation_secrets_reject_before_any_destination_write(self):
        canary = 'ghp_' + 'aB3dE6gH9jK2mN5pQ8sT1vW4yZ7bC0eF3hI6'
        for predicate in ('https://slsa.dev/provenance/v1', 'https://spdx.dev/Document'):
            manifest = {'config': {}, 'layers': [{'mediaType': 'application/vnd.in-toto+json'}]}
            with self.subTest(predicate=predicate), \
                 patch.object(publisher.subprocess, 'run') as command, \
                 patch.object(publisher.policy, 'verify_image'), \
                 patch.object(publisher, 'private_json', side_effect=[manifest, {}, manifest, {},
                     {'predicateType': predicate, 'predicate': {'buildNote': canary}}]):
                command.return_value.stdout = json.dumps(self.index()).encode()
                with self.assertRaisesRegex(ValueError, 'secret scan'):
                    publisher.publish(SOURCE, DESTINATION)
                self.assertEqual(1, command.call_count)
                self.assertNotIn(DESTINATION, command.call_args.args[0])

    def test_metadata_fetch_failure_prevents_publication(self):
        with patch.object(publisher.subprocess, 'run') as command, \
             patch.object(publisher.policy, 'verify_image'), \
             patch.object(publisher, 'private_json', side_effect=ValueError('checksum mismatch')):
            command.return_value.stdout = json.dumps(self.index()).encode()
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                publisher.publish(SOURCE, DESTINATION)
            self.assertEqual(1, command.call_count)

    def index(self):
        return {'schemaVersion': 2, 'manifests': [
            {'digest': DIGEST, 'platform': {'os': 'linux', 'architecture': 'amd64'}},
            {'platform': {'os': 'unknown', 'architecture': 'unknown'}, 'annotations': {
                'vnd.docker.reference.type': 'attestation-manifest', 'vnd.docker.reference.digest': DIGEST}}]}

    def test_unsafe_sources_and_destinations_fail_before_commands(self):
        for source, dest in [('registry.example/image@' + DIGEST, DESTINATION),
                             ('127.0.0.1:5000/image:mutable', DESTINATION),
                             ('127.0.0.1:99999/image@' + DIGEST, DESTINATION),
                             (SOURCE, '--option'), (SOURCE, 'docker.io/example/image')]:
            with self.subTest(source=source, dest=dest), patch.object(publisher.subprocess, 'run') as command:
                with self.assertRaises(ValueError):
                    publisher.publish(source, dest)
                command.assert_not_called()

    def test_failed_inspection_cannot_contact_destination(self):
        for failure in (ValueError('Forbidden production artifact'), subprocess.CalledProcessError(1, 'docker')):
            with self.subTest(failure=failure), patch.object(publisher.subprocess, 'run') as command, \
                 patch.object(publisher.policy, 'verify_image', side_effect=failure):
                command.return_value.stdout = json.dumps(self.index()).encode()
                with self.assertRaises(type(failure)):
                    publisher.publish(SOURCE, DESTINATION)
                self.assertEqual(1, command.call_count)
                self.assertIn(SOURCE, command.call_args.args[0])
                self.assertNotIn(DESTINATION, command.call_args.args[0])

    def test_uninspected_platforms_fail_before_copy(self):
        for platform in ({'os': 'linux', 'architecture': 'arm64'}, {'os': 'unknown', 'architecture': 'unknown'}):
            index = self.index()
            index['manifests'].append({'platform': platform})
            with patch.object(publisher.subprocess, 'run') as command, patch.object(publisher.policy, 'verify_image') as verify:
                command.return_value.stdout = json.dumps(index).encode()
                with self.assertRaisesRegex(ValueError, 'uninspected'):
                    publisher.publish(SOURCE, DESTINATION)
                verify.assert_not_called()
                self.assertEqual(1, command.call_count)

    def test_copy_preserves_all_digests_and_requires_remote_tls(self):
        events = []
        def command(args, **kwargs):
            events.append(args)
            return subprocess.CompletedProcess(args, 0, json.dumps(self.index()).encode() if '--raw' in args else DIGEST)
        with patch.object(publisher, 'inspect_oci_secrets',
                          side_effect=lambda *a: events.append('metadata verified')), \
             patch.object(publisher.subprocess, 'run', side_effect=command), \
             patch.object(publisher.policy, 'verify_image', side_effect=lambda *a, **k: events.append('verified')):
            self.assertEqual(DIGEST, publisher.publish(SOURCE, DESTINATION))
        self.assertEqual('verified', events[1])
        self.assertEqual('metadata verified', events[2])
        copy = events[3]
        for option in ('--all', '--preserve-digests', '--dest-tls-verify=true', '--src-tls-verify=false'):
            self.assertIn(option, copy)
        self.assertEqual(['docker://' + SOURCE, 'docker://' + DESTINATION], copy[-2:])

    def test_copy_failure_and_digest_mismatch_fail_closed(self):
        for fail_copy in (False, True):
            def command(args, **kwargs):
                if '--raw' in args:
                    return subprocess.CompletedProcess(args, 0, json.dumps(self.index()).encode())
                if args[0] == 'skopeo' and fail_copy:
                    raise subprocess.CalledProcessError(1, args)
                return subprocess.CompletedProcess(args, 0, 'sha256:' + 'b' * 64)
            with self.subTest(fail_copy=fail_copy), patch.object(publisher.subprocess, 'run', side_effect=command), \
                 patch.object(publisher.policy, 'verify_image'), patch.object(publisher, 'inspect_oci_secrets'):
                with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                    publisher.publish(SOURCE, DESTINATION)


if __name__ == '__main__':
    publisher.policy.secrets.install()
    unittest.main()
