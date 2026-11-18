#!/usr/bin/env python3
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
import urllib.error
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('qualification', Path(__file__).with_name('qualify-registry-publication.py'))
qualification = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qualification)


class QualificationTests(unittest.TestCase):
    def test_partial_upload_is_uncommitted_and_its_session_is_cancelled(self):
        repository = 'example/miningcore-release-qualification'
        source = '127.0.0.1:5000/fixture@sha256:' + 'a' * 64
        data = b'non-secret-random-fixture'
        digest = 'sha256:' + hashlib.sha256(data).hexdigest()
        descriptor = {'size': len(data), 'digest': digest}
        runtime = {'platform': {'os': 'linux', 'architecture': 'amd64'}}
        requests = []
        class Response(io.BytesIO):
            def __init__(self, data=b'', status=200, headers=None):
                super().__init__(data)
                self.status, self.headers = status, headers or {}
        def open_request(request, **kwargs):
            if isinstance(request, str):
                self.assertTrue(request.startswith('http://127.0.0.1:5000/'))
                return Response(data)
            method = request.get_method()
            requests.append(method)
            if request.full_url.startswith('https://ghcr.io/token?'):
                return Response(b'{"token":"fixture-only"}')
            if method == 'HEAD':
                raise urllib.error.HTTPError(request.full_url, 404, 'absent', {}, None)
            # GHCR returns a singular upload endpoint despite the plural POST.
            headers = {'Location': '/v2/' + repository + '/blobs/upload/session'}
            if method == 'PATCH':
                self.assertEqual(data[:len(data)//2], request.data)
                self.assertEqual('0-' + str(len(data)//2-1), request.headers['Content-range'])
                headers['Range'] = request.headers['Content-range']
            return Response(status=204 if method == 'DELETE' else 202, headers=headers)
        with patch.dict(os.environ, {'GITHUB_ACTOR': 'fixture', 'REGISTRY_TOKEN': 'fixture-only'}), \
             patch.object(qualification.urllib.request, 'build_opener') as opener, \
             patch.object(qualification.subprocess, 'run') as command, \
             patch.object(qualification.publisher, 'private_json', return_value={'layers': [descriptor]}):
            opener.return_value.open.side_effect = open_request
            command.return_value.stdout = json.dumps({'manifests': [runtime]}).encode()
            with qualification.incomplete_upload(source, 'ghcr.io/' + repository + ':qualification-1') as evidence:
                self.assertEqual(len(data)//2, evidence['partial_bytes_accepted'])
                self.assertNotIn('PUT', requests)
            self.assertTrue(evidence['upload_cancelled'])
            self.assertEqual(['GET', 'HEAD', 'POST', 'PATCH', 'HEAD', 'DELETE'], requests)

    def test_credentials_cannot_follow_an_upload_location_to_another_host_or_repository(self):
        repo = 'example/miningcore-release-qualification'
        good = '/v2/' + repo + '/blobs/uploads/session'
        self.assertEqual('https://ghcr.io' + good, qualification.upload_location(good, repo))
        singular = good.replace('/blobs/uploads/', '/blobs/upload/')
        self.assertEqual('https://ghcr.io' + singular, qualification.upload_location(singular, repo))
        for suffix in ('encoded.session==', 'encoded%3Dsession%3D', 'opaque+session'):
            location = good.rsplit('/', 1)[0] + '/' + suffix + '?state=fixture'
            self.assertEqual('https://ghcr.io' + location, qualification.upload_location(location, repo))
        for location in ('http://ghcr.io' + good, 'https://other.example' + good,
                         'https://ghcr.io@other.example' + good, '/v2/other/image/blobs/uploads/session',
                         good + '/../other', good + '%2f..%2fother', good + '%5cother'):
            with self.subTest(location=location), self.assertRaises(ValueError):
                qualification.upload_location(location, repo)

    def test_production_aliases_and_other_repositories_cannot_be_used(self):
        for destination in ('ghcr.io/ninjak1dd/miningcore:latest',
                            'docker.io/example/miningcore:qualification-1',
                            'ghcr.io/ninjak1dd/miningcore-release-qualification:latest'):
            with self.subTest(destination=destination), patch.object(qualification.subprocess, 'run') as command:
                with self.assertRaises(ValueError):
                    qualification.qualify('unused', destination)
                command.assert_not_called()

    def test_download_requires_actual_hashed_provenance_and_sbom_payloads(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'blobs/sha256').mkdir(parents=True)
            def write(value):
                raw = json.dumps(value).encode()
                digest = hashlib.sha256(raw).hexdigest()
                (root / 'blobs/sha256' / digest).write_bytes(raw)
                return {'digest': 'sha256:' + digest, 'size': len(raw)}
            runtime = write({})
            runtime['platform'] = {'os': 'linux', 'architecture': 'amd64'}
            predicates = [write({'subject': [{'digest': {'sha256': runtime['digest'][7:]}}], 'predicateType': kind, 'predicate': {}}) for kind in
                          ('https://slsa.dev/provenance/v1', 'https://spdx.dev/Document')]
            attestation = write({'layers': predicates})
            attestation['annotations'] = {'vnd.docker.reference.type': 'attestation-manifest'}
            index = write({'manifests': [runtime, attestation]})['digest']
            rows = qualification.inspect_download(root, index)
            self.assertEqual(2, len(rows))
            self.assertEqual({row['digest'] for row in predicates}, {row['payload_digest'] for row in rows})
            (root / 'blobs/sha256' / predicates[0]['digest'][7:]).write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                qualification.inspect_download(root, index)


if __name__ == '__main__':
    unittest.main()
