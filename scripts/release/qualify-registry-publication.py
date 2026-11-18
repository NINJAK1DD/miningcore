#!/usr/bin/env python3
"""Explicit GHCR qualification on unique disposable tags, never production aliases."""
import argparse
import base64
from contextlib import contextmanager
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.error
import urllib.parse
import urllib.request

spec = importlib.util.spec_from_file_location('publisher', Path(__file__).with_name('publish-verified-image.py'))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)
phase = 'initialization'


class QualificationError(ValueError):
    """Reviewed static diagnostic; never include response bodies, URLs or credentials."""



def upload_location(location, repository):
    if any(part in ('.', '..') for part in urllib.parse.unquote(urllib.parse.urlsplit(location).path).split('/')):
        raise QualificationError('Refusing a traversing upload location')
    location = urllib.parse.urljoin('https://ghcr.io', location)
    parsed = urllib.parse.urlsplit(location)
    if (parsed.scheme != 'https' or parsed.netloc != 'ghcr.io' or parsed.fragment or
            not parsed.path.startswith('/v2/' + repository + '/blobs/uploads/')):
        raise QualificationError('Refusing an upload location outside the qualification repository')
    # OCI upload identifiers are opaque, not necessarily UUIDs. GHCR may use
    # encoded/padded tokens. Accept a single safe segment while preserving the
    # exact server-provided URL/query and the fixed origin/repository boundary.
    suffix = urllib.parse.unquote(parsed.path.removeprefix('/v2/' + repository + '/blobs/uploads/'))
    if not re.fullmatch(r'[A-Za-z0-9_.~+=-]+', suffix) or suffix in ('.', '..'):
        raise QualificationError('Refusing unsafe upload session syntax')
    return location


@contextmanager
def incomplete_upload(source, destination):
    """Interrupt a real chunked upload, then let the normal publisher recover."""
    global phase
    phase = 'obtain-repository-token'
    repository = destination.split(':', 1)[0].removeprefix('ghcr.io/')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), publisher.NoRedirect())
    credentials = (os.environ['GITHUB_ACTOR'] + ':' + os.environ['REGISTRY_TOKEN']).encode()
    query = urllib.parse.urlencode({'service': 'ghcr.io', 'scope': 'repository:' + repository + ':pull,push'})
    request = urllib.request.Request('https://ghcr.io/token?' + query,
        headers={'Authorization': 'Basic ' + base64.b64encode(credentials).decode()})
    with opener.open(request, timeout=60) as response:
        token = json.loads(response.read(1024 * 1024))['token']
    def request(method, url, body=None, headers=None):
        request_headers = {'Authorization': 'Bearer ' + token, **(headers or {})}
        return opener.open(urllib.request.Request(url, data=body, headers=request_headers, method=method), timeout=60)
    index = json.loads(subprocess.run(['docker', 'buildx', 'imagetools', 'inspect', source, '--raw'],
                                     check=True, capture_output=True, timeout=60).stdout)
    runtime = next(m for m in index['manifests'] if m['platform'] == {'os': 'linux', 'architecture': 'amd64'})
    manifest = publisher.private_json(source, 'manifests', runtime)
    layer = max(manifest['layers'], key=lambda item: item['size'])
    if not re.fullmatch(r'sha256:[0-9a-f]{64}', layer['digest']) or not 0 < layer['size'] <= 16 * 1024**2:
        raise QualificationError('Qualification requires a bounded fresh synthetic layer')
    authority, private_repository = source.split('@', 1)[0].split('/', 1)
    with opener.open(f'http://{authority}/v2/{private_repository}/blobs/{layer["digest"]}', timeout=60) as response:
        data = response.read(layer['size'] + 1)
    if len(data) != layer['size'] or 'sha256:' + hashlib.sha256(data).hexdigest() != layer['digest']:
        raise QualificationError('Synthetic layer checksum mismatch')
    blob_url = f'https://ghcr.io/v2/{repository}/blobs/{layer["digest"]}'
    def require_absent():
        try:
            with request('HEAD', blob_url):
                raise QualificationError('Partial upload fixture must not already be committed')
        except urllib.error.HTTPError as error:
            try:
                if error.code != 404:
                    raise
            finally:
                error.close()
    phase = 'check-fresh-blob-absence'
    require_absent()
    location = None
    evidence = {'blob_digest': layer['digest'], 'blob_bytes': len(data), 'upload_cancelled': False}
    try:
        phase = 'start-partial-upload'
        with request('POST', f'https://ghcr.io/v2/{repository}/blobs/uploads/', b'') as response:
            if response.status != 202:
                raise QualificationError('Registry did not start a chunked upload')
            location = upload_location(response.headers['Location'], repository)
            length = max(len(data) // 2, int(response.headers.get('OCI-Chunk-Min-Length', '1')))
        if length >= len(data):
            raise QualificationError('Synthetic layer is too small for the registry minimum chunk size')
        phase = 'send-partial-upload'
        with request('PATCH', location, data[:length], {'Content-Type': 'application/octet-stream',
                     'Content-Range': f'0-{length - 1}'}) as response:
            if response.status != 202 or response.headers.get('Range') != f'0-{length - 1}':
                raise QualificationError('Registry did not confirm the expected incomplete byte range')
            location = upload_location(response.headers['Location'], repository)
        phase = 'verify-uncommitted-blob-absence'
        require_absent()  # No closing PUT: uploaded bytes must not form a visible blob.
        evidence['partial_bytes_accepted'] = length
        yield evidence
    finally:
        if location:
            try:
                with request('DELETE', location, b'') as response:
                    evidence['upload_cancelled'] = response.status == 204
            except (OSError, ValueError):
                # OCI specifies best-effort cancellation and server expiry.
                # Record failure rather than claiming that cleanup succeeded.
                pass


def inspect_download(directory, digest):
    def blob(descriptor):
        value = descriptor['digest']
        if not re.fullmatch(r'sha256:[0-9a-f]{64}', value):
            raise QualificationError('Invalid downloaded OCI digest')
        raw = (directory / 'blobs/sha256' / value[7:]).read_bytes()
        if hashlib.sha256(raw).hexdigest() != value[7:]:
            raise QualificationError('Downloaded OCI content hash mismatch')
        return json.loads(raw)
    index = blob({'digest': digest})
    runtimes = [m for m in index['manifests'] if m.get('platform') == {'os': 'linux', 'architecture': 'amd64'}]
    if len(runtimes) != 1:
        raise QualificationError('Downloaded index does not identify one qualified runtime')
    runtime_digest = runtimes[0]['digest'].removeprefix('sha256:')
    attestations = []
    for descriptor in index['manifests']:
        manifest = blob(descriptor)
        if descriptor.get('annotations', {}).get('vnd.docker.reference.type') != 'attestation-manifest':
            continue
        for layer in manifest['layers']:
            payload = blob(layer)
            if not any(subject.get('digest', {}).get('sha256') == runtime_digest for subject in payload.get('subject', [])):
                raise QualificationError('Downloaded attestation subject differs from the qualified runtime')
            attestations.append({'manifest_digest': descriptor['digest'], 'payload_digest': layer['digest'],
                                 'predicate_type': payload['predicateType']})
    predicates = {item['predicate_type'] for item in attestations}
    if 'https://spdx.dev/Document' not in predicates or not any(p.startswith('https://slsa.dev/provenance/') for p in predicates):
        raise QualificationError('Downloaded provenance or SBOM is missing')
    return attestations


def qualify(source, destination):
    global phase
    if not re.fullmatch(r'ghcr\.io/[a-z0-9][a-z0-9-]*/miningcore-release-qualification:qualification-[a-z0-9-]+', destination):
        raise QualificationError('Qualification requires a dedicated GHCR repository and unique qualification tag')
    publisher.policy.secrets.install()
    # Inspect before *any* remote blob write, including the partial-upload probe.
    phase = 'inspect-private-candidate'
    publisher.publish(source, source.split('@', 1)[0] + ':qualified-private')
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
    with incomplete_upload(source, destination) as partial:
        phase = 'publish-with-lost-response'
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
            raise QualificationError('Qualification did not exercise lost-copy-response recovery')
        phase = 'retry-immutable-publication'
        digest = publisher.publish(source, destination)
    with tempfile.TemporaryDirectory(prefix='miningcore-registry-qualification-') as temporary:
        directory = Path(temporary)
        command = ['skopeo', 'copy', '--all', '--preserve-digests', '--src-tls-verify=true']
        auth = Path(os.environ.get('DOCKER_CONFIG', str(Path.home() / '.docker'))) / 'config.json'
        if auth.is_file():
            command += ['--src-authfile', str(auth)]
        # Independent authenticated download; do not inspect the local preview/cache.
        phase = 'retrieve-public-attestations'
        subprocess.run(command + ['docker://' + destination.split(':', 1)[0] + '@' + digest,
                                  'oci:' + str(directory) + ':qualification'], check=True, timeout=600)
        attestations = inspect_download(directory, digest)
        blob = directory / 'blobs/sha256' / partial['blob_digest'][7:]
        if hashlib.sha256(blob.read_bytes()).hexdigest() != partial['blob_digest'][7:]:
            raise QualificationError('Recovered partial-upload layer did not download with the expected hash')
    return {'destination': destination, 'index_digest': digest, 'attestations': attestations,
            'lost_copy_response_recovery': True, 'partial_blob_upload_interruption_tested': True,
            'partial_upload': partial,
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
    try:
        result = qualify(args.source, args.destination)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        # Never put authentication headers, upload session URLs or raw server
        # responses into public logs. Missing evidence means failed qualification.
        status = f'; HTTP {error.code}' if isinstance(error, urllib.error.HTTPError) else ''
        if isinstance(error, QualificationError):
            status += '; ' + str(error)
        parser.exit(1, f'Registry qualification failed at {phase} ({type(error).__name__}{status}); production remains disabled.\n')
    with open(args.report, 'x', encoding='utf-8') as output:
        json.dump(result, output, indent=2)
        output.write('\n')
    print('Qualification evidence recorded; production remains disabled pending review.')
