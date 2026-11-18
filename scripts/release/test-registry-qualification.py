#!/usr/bin/env python3
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('qualification', Path(__file__).with_name('qualify-registry-publication.py'))
qualification = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qualification)


class QualificationTests(unittest.TestCase):
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
            predicates = [write({'predicateType': kind, 'predicate': {}}) for kind in
                          ('https://slsa.dev/provenance/v1', 'https://spdx.dev/Document')]
            attestation = write({'layers': predicates})
            attestation['annotations'] = {'vnd.docker.reference.type': 'attestation-manifest'}
            index = write({'manifests': [attestation]})['digest']
            rows = qualification.inspect_download(root, index)
            self.assertEqual(2, len(rows))
            self.assertEqual({row['digest'] for row in predicates}, {row['payload_digest'] for row in rows})
            (root / 'blobs/sha256' / predicates[0]['digest'][7:]).write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                qualification.inspect_download(root, index)


if __name__ == '__main__':
    unittest.main()
