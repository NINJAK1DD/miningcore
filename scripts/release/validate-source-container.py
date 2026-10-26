#!/usr/bin/env python3
"""Run isolated cold/warm source-image and unchanged workflow runtime checks."""
import argparse
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import time
import uuid

import yaml


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root, output = args.root.resolve(), args.output.resolve()
    if output.exists() or root / 'build' not in output.parents:
        parser.error('Use a new evidence directory inside the checkout build directory')
    workflow = yaml.load((root / '.github/workflows/release.yml').read_text(), Loader=yaml.BaseLoader)
    steps = workflow['jobs']['source-container-check']['steps']
    smoke = next(step['run'] for step in steps if step['name'] == 'Test protected container listeners from host loopback')
    # The workflow owns these fixture names. Refuse to delete pre-existing lab resources.
    names = set(subprocess.check_output(['docker', 'ps', '-a', '--format', '{{.Names}}'], text=True).splitlines())
    reserved = {'miningcore-api-smoke', 'miningcore-api-unconfigured', 'miningcore-api-shared',
                'miningcore-api-rate-limit', 'miningcore-api-nginx'}
    networks = subprocess.check_output(['docker', 'network', 'ls', '--format', '{{.Name}}'], text=True).splitlines()
    if names & reserved or 'miningcore-api-smoke' in networks:
        parser.error('The lab already has a reserved container/listener test resource')
    for port in set(re.findall(r'--publish 127\.0\.0\.1:(\d+):', smoke)):
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', int(port)))
    exported = root / 'build/container-runtime-probes'
    if exported.exists():
        parser.error('Move the previous build/container-runtime-probes export before testing')
    output.mkdir(parents=True)
    token = uuid.uuid4().hex[:12]
    builder = 'issue223-' + token
    evidence = {'schema': 1, 'source_commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
                'runs': [], 'complete': False}

    def run(name, command, seconds=2400, environment=None, build=False, input_file=None):
        print('validation_phase start ' + name, flush=True)
        vertices = {}
        truncated = False
        started = time.monotonic()
        with (output / (name + '.log')).open('wb') as log:
            process = subprocess.Popen(['timeout', '--signal=TERM', '--kill-after=30s', str(seconds), *command],
                                       cwd=root, env=environment, stdin=input_file,
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            size = 0
            while chunk := process.stdout.readline(65536):
                if size + len(chunk) <= 64 * 1024 * 1024:
                    log.write(chunk)
                    size += len(chunk)
                else:
                    truncated = True
                if build:
                    line = chunk.decode('utf-8', errors='replace').strip()
                    match = re.match(r'^#(\d+) (.*)$', line)
                    if match:
                        key, text = match.groups()
                        if key not in vertices and len(vertices) < 128:
                            vertices[key] = {'name': text[:200], 'cached': False}
                        if key in vertices:
                            if text == 'CACHED': vertices[key]['cached'] = True
                            done = re.fullmatch(r'DONE ([0-9.]+)s', text)
                            if done: vertices[key]['elapsed_seconds'] = float(done[1])
            status = process.wait()
        record = {'phase': name, 'elapsed_seconds': round(time.monotonic() - started, 3),
                  'status': status, 'log_truncated': truncated}
        if build: record['vertices'] = list(vertices.values())
        evidence['runs'].append(record)
        print('validation_phase complete ' + json.dumps({k: v for k, v in record.items() if k != 'vertices'}), flush=True)
        (output / 'evidence.json').write_text(json.dumps(evidence, indent=2) + '\n')
        if status: raise subprocess.CalledProcessError(status, name)
        return record

    created = False
    try:
        run('create-builder', ['docker', 'buildx', 'create', '--name', builder, '--driver', 'docker-container'], 120)
        created = True
        run('bootstrap-builder', ['docker', 'buildx', 'inspect', '--builder', builder, '--bootstrap'], 180)
        for mode in ('cold', 'warm'):
            suffix = 'issue223-' + token + '-' + mode
            image = 'miningcore-source-test:' + suffix
            command = ['docker', 'buildx', 'build', '--builder', builder, '--progress=plain', '--load', '--tag', image]
            if mode == 'cold': command.append('--no-cache')
            build_record = run(mode + '-image', [*command, '.'], build=True)
            probe_record = run(mode + '-probes', ['docker', 'buildx', 'build', '--builder', builder, '--progress=plain',
                               '--target', 'runtime-probes', '--output', 'type=local,dest=' + str(exported), '.'], 600, build=True)
            if mode == 'warm':
                if not any(v['cached'] and 'dotnet publish -c Release' in v['name'] for v in build_record['vertices']):
                    raise ValueError('Warm production publish did not reuse its validated cache')
                if not any(v['cached'] and 'publish-container-probes.py' in v['name'] for v in probe_record['vertices']):
                    raise ValueError('Warm probe build did not reuse its validated cache')
            probe_evidence = json.loads((exported / 'build-evidence.json').read_text())
            if not probe_evidence.get('complete'):
                raise ValueError('Probe export is incomplete')
            run(mode + '-image-checksums', ['docker', 'run', '--rm', '--entrypoint', 'sh', image,
                                          '-c', 'cd /app && sha256sum Miningcore.dll ./*.so'], 120)
            with (output / (mode + '-image-checksums.log')).open('rb') as checksums:
                run(mode + '-provenance', ['python3', 'scripts/release/verify-container-probes.py',
                                         str(exported / 'build-evidence.json')], 30, input_file=checksums)
            run(mode + '-version', ['docker', 'run', '--rm', image, '--version'], 120)
            run(mode + '-zeromq', ['docker', 'run', '--rm', '--volume', str(exported / 'zeromq-runtime-probe') + ':/probe:ro',
                                 '--entrypoint', 'dotnet', image, '/probe/ZeroMqRuntimeProbe.dll'], 120)
            script = smoke.replace('${{ github.sha }}', suffix)
            if '${{' in script: raise ValueError('Unsupported workflow expression in runtime check')
            script_path = output / (mode + '-listeners.sh')
            script_path.write_text(script)
            run(mode + '-listeners', ['bash', str(script_path)], 300, dict(os.environ, GITHUB_WORKSPACE=str(root)))
            exported.rename(output / (mode + '-probes'))
        evidence['complete'] = True
        return 0
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print('Source-container validation failed: ' + str(error), file=sys.stderr)
        return 1
    finally:
        if created:
            try:
                cleanup = subprocess.run(['docker', 'buildx', 'rm', builder], stdout=subprocess.DEVNULL,
                                         timeout=60, check=False)
                evidence['builder_cleanup_status'] = cleanup.returncode
            except (OSError, subprocess.TimeoutExpired):
                evidence['builder_cleanup_status'] = 'failed'
                print('Could not remove isolated lab builder: ' + builder, file=sys.stderr)
        (output / 'evidence.json').write_text(json.dumps(evidence, indent=2) + '\n')


if __name__ == '__main__':
    sys.exit(main())
