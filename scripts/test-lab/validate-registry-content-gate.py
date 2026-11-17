#!/usr/bin/env python3
"""Disposable private/public registry boundary test; requires Buildx, Skopeo and PyYAML."""
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import urllib.request
import urllib.error
import uuid

import yaml

ROOT = Path(__file__).resolve().parents[2]
REGISTRY = 'registry@sha256:82f6ff4a7f982960c8aa51617fea3aabc485df6e55cce80d22deb1de36f1bc3c'


def run(*args, **kwargs):
    result = subprocess.run(args, text=True, capture_output=True, timeout=600, **kwargs)
    if result.returncode:
        raise RuntimeError(f'{args[0]} failed: {result.stderr[-3000:]}')
    return result.stdout.strip()


def main():
    if os.geteuid() == 0:
        raise SystemExit('Run as the ordinary lab user, not root')
    def terminate(signum, frame):
        raise SystemExit(128 + signum)
    signal.signal(signal.SIGTERM, terminate)
    name = 'miningcore-registry-check-' + uuid.uuid4().hex
    images = []
    registries = [name + '-private', name + '-public']
    with tempfile.TemporaryDirectory(prefix=name) as temporary:
        scratch = Path(temporary)
        try:
            ports = []
            for registry in registries:
                run('docker', 'run', '-d', '--name', registry, '--read-only', '--cap-drop=ALL',
                    '--security-opt=no-new-privileges', '--user=1000:1000',
                    '--tmpfs=/var/lib/registry:rw,nosuid,nodev,size=67108864,mode=1777',
                    '-p', '127.0.0.1::5000', REGISTRY)
                port = run('docker', 'port', registry, '5000/tcp').rsplit(':', 1)[1]
                ports.append(port)
                for attempt in range(30):
                    try:
                        with urllib.request.urlopen(f'http://127.0.0.1:{port}/v2/', timeout=2) as response:
                            if response.status == 200:
                                break
                    except OSError:
                        time.sleep(0.2)
                else:
                    raise RuntimeError('Disposable registry did not become ready')
            repository = f'127.0.0.1:{ports[0]}/fixture'
            destination = f'127.0.0.1:{ports[1]}/fixture'
            config = scratch / 'buildkit.toml'
            config.write_text(f'[registry."127.0.0.1:{ports[0]}"]\n  http = true\n')
            run('docker', 'buildx', 'create', '--name', name, '--driver', 'docker-container',
                '--driver-opt', 'network=host', '--buildkitd-config', str(config))

            hub = yaml.load((ROOT / '.github/workflows/docker-image.yml').read_text(), Loader=yaml.BaseLoader)
            release = yaml.load((ROOT / '.github/workflows/release.yml').read_text(), Loader=yaml.BaseLoader)
            hub_steps = hub['jobs']['build']['steps']
            promote = next(s['run'] for s in hub_steps if s['name'] == 'Promote and record verified Docker Hub image digest')
            ghcr_gate = next(s['run'] for s in release['jobs']['publish-release']['steps']
                             if s['name'] == 'Inspect private candidate and publish verified staged container')
            # Execute the real gate/promotion commands against only this private
            # fixture repository. No external registry credentials or tags are used.
            promote = promote.replace('127.0.0.1:5000/miningcore', repository).replace('docker.io/theretromike/miningcore', destination)
            ghcr_gate = ghcr_gate.replace('127.0.0.1:5000/miningcore', repository)
            env = dict(os.environ, DESTINATION_IMAGE=destination + ':ghcr', GITHUB_STEP_SUMMARY=str(scratch / 'summary'),
                       GITHUB_OUTPUT=str(scratch / 'outputs'))
            digests = {}
            for kind in ('clean', 'contaminated'):
                context = scratch / kind
                context.mkdir()
                (context / 'Miningcore').write_text('non-executable fixture')
                dockerfile = 'FROM scratch\nCOPY Miningcore /app/Miningcore\n'
                if kind == 'contaminated':
                    (context / 'private.trx').write_text('fixture-only, no secrets')
                    dockerfile += 'COPY private.trx /tmp/private.trx\n'
                (context / 'Dockerfile').write_text(dockerfile)
                metadata = scratch / f'{kind}.json'
                tag = repository + ':' + kind
                images.append(tag)
                run('docker', 'buildx', 'build', '--builder', name, '--platform=linux/amd64', '--provenance=mode=max',
                    '--sbom=true', '--push', '--tag', tag, '--metadata-file', str(metadata), str(context))
                digest = json.loads(metadata.read_text())['containerimage.digest']
                digests[kind] = digest
                images.append(repository + '@' + digest)
                env['IMAGE_DIGEST'] = digest
                # Exercise the publisher itself: failed inspection must prevent
                # public writes without relying on a redundant preceding gate.
                result = subprocess.run(['bash', '-euo', 'pipefail', '-c', promote],
                                        cwd=ROOT, env=env, text=True, capture_output=True, timeout=600)
                ghcr = subprocess.run(['bash', '-euo', 'pipefail', '-c', ghcr_gate],
                                      cwd=ROOT, env=env, text=True, capture_output=True, timeout=600)
                if kind == 'clean':
                    if result.returncode or ghcr.returncode:
                        raise RuntimeError(result.stderr[-3000:] + ghcr.stderr[-3000:])
                    original = run('docker', 'buildx', 'imagetools', 'inspect', repository + '@' + digest, '--raw')
                    promoted = run('docker', 'buildx', 'imagetools', 'inspect', destination + ':latest', '--raw')
                    if original != promoted:
                        raise RuntimeError('Promotion changed the inspected image index')
                    index = json.loads(original)
                    attestations = [m for m in index['manifests'] if m.get('annotations', {}).get(
                        'vnd.docker.reference.type') == 'attestation-manifest']
                    if not attestations:
                        raise RuntimeError('Fixture lacks registry-attached attestations')
                    predicates = set()
                    for item in attestations:
                        manifest = json.loads(run('docker', 'buildx', 'imagetools', 'inspect',
                                                  destination + '@' + item['digest'], '--raw'))
                        predicates.update(layer.get('annotations', {}).get('in-toto.io/predicate-type')
                                          for layer in manifest['layers'])
                    if not any(p and 'slsa.dev/provenance/' in p for p in predicates) or 'https://spdx.dev/Document' not in predicates:
                        raise RuntimeError('Expected both provenance and SBOM attestations')
                elif (result.returncode == 0 or ghcr.returncode == 0 or
                      'Forbidden production artifact' not in result.stderr or
                      'Forbidden production artifact' not in ghcr.stderr):
                    raise RuntimeError('Contaminated staged image was not rejected by both gates')
                if kind == 'contaminated':
                    index = json.loads(run('docker', 'buildx', 'imagetools', 'inspect', repository + '@' + digest, '--raw'))
                    runtime = next(m for m in index['manifests'] if m['platform']['os'] == 'linux')
                    manifest = json.loads(run('docker', 'buildx', 'imagetools', 'inspect', repository + '@' + runtime['digest'], '--raw'))
                    # The dirty layer and its index must never reach the public side,
                    # even though the private candidate was built successfully.
                    for path in ('manifests/' + digest, 'blobs/' + manifest['layers'][-1]['digest']):
                        try:
                            urllib.request.urlopen(f'http://127.0.0.1:{ports[1]}/v2/fixture/{path}', timeout=3)
                        except urllib.error.HTTPError as error:
                            if error.code != 404:
                                raise
                        else:
                            raise RuntimeError('Rejected candidate content reached the public registry')
                current = run('docker', 'buildx', 'imagetools', 'inspect', destination + ':latest',
                              '--format', '{{.Manifest.Digest}}')
                if current != digests['clean']:
                    raise RuntimeError('Contaminated registry image replaced the verified latest digest')
            print('PASS: rejected candidate manifest and layer never reach public registry; '
                  'clean promotion preserves the index, provenance and SBOM')
        finally:
            subprocess.run(['docker', 'buildx', 'rm', '-f', name], capture_output=True, timeout=60)
            for reference in images:
                subprocess.run(['docker', 'image', 'rm', reference], capture_output=True, timeout=30)
            for registry in registries:
                subprocess.run(['docker', 'rm', '-f', registry], capture_output=True, timeout=30)


if __name__ == '__main__':
    main()
