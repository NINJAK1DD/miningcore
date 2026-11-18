#!/usr/bin/env python3
"""Explicit GHCR qualification on unique disposable tags, never production aliases."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

spec = importlib.util.spec_from_file_location('publisher', Path(__file__).with_name('publish-verified-image.py'))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


def inspect_download(directory, digest):
    def blob(descriptor):
        value = descriptor['digest']
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', value):
            raise ValueError('Invalid downloaded OCI digest')
        raw = (directory / 'blobs/sha256' / value[7:]).read_bytes()
        if hashlib.sha256(raw).hexdigest() != value[7:]:
            raise ValueError('Downloaded OCI content hash mismatch')
        return json.loads(raw)
    index = blob({'digest': digest})
    attestations = []
    for descriptor in index['manifests']:
        manifest = blob(descriptor)
        if descriptor.get('annotations', {}).get('vnd.docker.reference.type') != 'attestation-manifest':
            continue
        for layer in manifest['layers']:
            payload = blob(layer)
            attestations.append({'manifest_digest': descriptor['digest'], 'payload_digest': layer['digest'],
                                 'predicate_type': payload['predicateType']})
    predicates = {item['predicate_type'] for item in attestations}
    if 'https://spdx.dev/Document' not in predicates or not any(p.startswith('https://slsa.dev/provenance/') for p in predicates):
        raise ValueError('Downloaded provenance or SBOM is missing')
    return attestations


def qualify(source, destination):
    if not re.fullmatch(r'ghcr\.io/[a-z0-9][a-z0-9-]*/miningcore-release-qualification:qualification-[a-z0-9-]+', destination):
        raise ValueError('Qualification requires a dedicated GHCR repository and unique qualification tag')
    publisher.policy.secrets.install()
    real_run = subprocess.run
    interrupted = False
    def lose_copy_response(command, **kwargs):
        nonlocal interrupted
        result = real_run(command, **kwargs)
        if command[:2] == ['skopeo', 'copy'] and command[-1] == 'docker://' + destination:
            interrupted = True
            raise ConnectionError('Injected lost response after completed registry copy')
        return result
    # Rehearse the uncertain boundary: upload succeeded but the publisher never
    # saw completion/recorded a digest. Retry the same immutable source and tag.
    publisher.subprocess.run = lose_copy_response
    try:
        try:
            publisher.publish(source, destination)
        except ConnectionError:
            if not interrupted:
                raise
    finally:
        publisher.subprocess.run = real_run
    if not interrupted:
        raise ValueError('Qualification did not exercise lost-copy-response recovery')
    digest = publisher.publish(source, destination)
    with tempfile.TemporaryDirectory(prefix='miningcore-registry-qualification-') as temporary:
        directory = Path(temporary)
        command = ['skopeo', 'copy', '--all', '--preserve-digests', '--src-tls-verify=true']
        auth = Path(os.environ.get('DOCKER_CONFIG', str(Path.home() / '.docker'))) / 'config.json'
        if auth.is_file():
            command += ['--src-authfile', str(auth)]
        # Independent authenticated download; do not inspect the local preview/cache.
        subprocess.run(command + ['docker://' + destination.split(':', 1)[0] + '@' + digest,
                                  'oci:' + str(directory) + ':qualification'], check=True, timeout=600)
        attestations = inspect_download(directory, digest)
    return {'destination': destination, 'index_digest': digest, 'attestations': attestations,
            'lost_copy_response_recovery': True, 'partial_blob_upload_interruption_tested': False,
            'production_publication_enabled': False,
            'declared_workflow_permissions': ['contents:read', 'packages:write'],
            'operator_permission_review_required': True,
            'run': os.environ.get('GITHUB_RUN_ID'), 'source_commit': os.environ.get('GITHUB_SHA')}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True)
    parser.add_argument('--destination', required=True)
    parser.add_argument('--report', required=True)
    args = parser.parse_args()
    result = qualify(args.source, args.destination)
    with open(args.report, 'x', encoding='utf-8') as output:
        json.dump(result, output, indent=2)
        output.write('\n')
    print('Qualification evidence recorded; production remains disabled pending review.')
