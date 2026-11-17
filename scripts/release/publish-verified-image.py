#!/usr/bin/env python3
"""Copy an inspected runner-private image to a registry without rebuilding it."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile

spec = importlib.util.spec_from_file_location('production_contents', Path(__file__).with_name('verify-production-contents.py'))
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


def loopback(reference):
    match = re.match(r'^127\.0\.0\.1:([0-9]+)/', reference)
    return bool(match and 0 < int(match[1]) < 65536)


def publish(source, destination):
    if not loopback(source) or not re.fullmatch(r'127\.0\.0\.1:[0-9]+/[a-z0-9._/-]+@sha256:[0-9a-f]{64}', source):
        raise ValueError('Source must be an immutable digest in the runner loopback registry')
    if not re.fullmatch(r'[a-z0-9][a-z0-9.:-]*/[a-z0-9._/-]+:[A-Za-z0-9_][A-Za-z0-9_.-]*', destination):
        raise ValueError('Destination must be a registry/repository:tag reference')
    digest = source.rsplit('@', 1)[1]
    # The scanner inspects linux/amd64. Refuse additional runtime platforms
    # before --all can copy any uninspected platform into the public registry.
    result = subprocess.run(['docker', 'buildx', 'imagetools', 'inspect', source, '--raw'],
                            check=True, capture_output=True, timeout=60)
    index = json.loads(result.stdout)
    manifests = index.get('manifests', [])
    runtime = [m for m in manifests if m.get('platform') == {'architecture': 'amd64', 'os': 'linux'}]
    if index.get('schemaVersion') != 2 or len(runtime) != 1:
        raise ValueError('Expected one linux/amd64 runtime in the candidate index')
    for entry in manifests:
        if entry is runtime[0]:
            continue
        annotations = entry.get('annotations', {})
        if (entry.get('platform') != {'architecture': 'unknown', 'os': 'unknown'} or
                annotations.get('vnd.docker.reference.type') != 'attestation-manifest' or
                annotations.get('vnd.docker.reference.digest') != runtime[0]['digest']):
            raise ValueError('Candidate index contains an uninspected platform or unrelated attestation')
    policy.verify_image(source, pull=True)
    # No destination write, not even a public staging upload, precedes the check.
    command = ['skopeo', 'copy', '--all', '--preserve-digests', '--src-no-creds', '--src-tls-verify=false',
               '--dest-tls-verify=' + ('false' if loopback(destination) else 'true')]
    auth = Path(os.environ.get('DOCKER_CONFIG', str(Path.home() / '.docker'))) / 'config.json'
    if auth.is_file():
        command += ['--dest-authfile', str(auth)]
    subprocess.run(command + ['docker://' + source, 'docker://' + destination], check=True, timeout=600)
    actual = subprocess.run(['docker', 'buildx', 'imagetools', 'inspect', destination,
                             '--format', '{{.Manifest.Digest}}'], check=True, capture_output=True, text=True, timeout=60)
    if actual.stdout.strip() != digest:
        raise ValueError('Published index digest differs from the inspected candidate')
    return digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True)
    parser.add_argument('--destination', required=True)
    args = parser.parse_args()
    try:
        digest = publish(args.source, args.destination)
        if os.environ.get('GITHUB_OUTPUT'):
            with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as output:
                output.write(f'digest={digest}\n')
        print(f'Published inspected image: {args.destination}@{digest}')
    except (OSError, ValueError, KeyError, TypeError, AttributeError, tarfile.TarError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Verified publication failed: {error}\n')


if __name__ == '__main__':
    main()
