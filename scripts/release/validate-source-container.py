#!/usr/bin/env python3
"""Run isolated cold/warm source-image and unchanged workflow runtime checks."""
import argparse
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import sys
import time
import uuid

import yaml


def clean_source(root, expected=None):
    # These Docker exclusions use the shared Git/Docker wildcard subset. Do not
    # use --exclude-standard: git-ignored native outputs can still enter Docker.
    def git(*arguments):
        return subprocess.check_output(['git', '--no-optional-locks', *arguments], cwd=root, timeout=30)

    commit = git('rev-parse', 'HEAD').decode().strip()
    tracked = git('status', '--porcelain=v1', '-z', '--untracked-files=no')
    untracked = git('ls-files', '--others', '--exclude-from=.dockerignore', '-z')
    if tracked or untracked or (expected is not None and commit != expected):
        raise ValueError('Exact-commit validation requires clean tracked files and no untracked Docker inputs')
    return commit


def remove_builder(builder):
    try:
        return subprocess.run(['docker', 'buildx', 'rm', builder], stdout=subprocess.DEVNULL,
                              timeout=60, check=False).returncode
    except (OSError, subprocess.TimeoutExpired):
        return 'failed'


def finish(evidence, functional_passed, cleanup_status, result):
    evidence['functional_validation_passed'] = functional_passed
    evidence['builder_cleanup_status'] = cleanup_status
    evidence['complete'] = functional_passed and cleanup_status == 0
    return result if result else (0 if evidence['complete'] else 1)


def interrupted(signum, frame):
    raise KeyboardInterrupt


def require_publish_reuse(record):
    if not any(vertex['cached'] and 'dotnet publish -c Release' in vertex['name']
               for vertex in record['vertices']):
        raise ValueError('Probe export did not reuse the audited production publish')


def listener_environment(script, root, source_sha):
    if '${{' in script:
        raise ValueError('Unsupported workflow expression in runtime check')
    return dict(os.environ, GITHUB_WORKSPACE=str(root), SOURCE_SHA=source_sha)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root, output = args.root.resolve(), args.output.resolve()
    if output.exists() or root / 'build' not in output.parents:
        parser.error('Use a new evidence directory inside the checkout build directory')
    try:
        source_commit = clean_source(root)
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        parser.error(str(error))
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
    evidence = {'schema': 1, 'source_commit': source_commit, 'source_tree_clean': True,
                'runs': [], 'complete': False}

    def run(name, command, seconds=2400, environment=None, build=False, input_file=None):
        print('validation_phase start ' + name, flush=True)
        vertices = {}
        truncated = False
        started = time.monotonic()
        with (output / (name + '.log')).open('wb') as log:
            process = subprocess.Popen(['timeout', '--signal=TERM', '--kill-after=30s', str(seconds), *command],
                                       cwd=root, env=environment, stdin=input_file,
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, start_new_session=True)
            size = 0
            try:
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
            except KeyboardInterrupt:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                raise
        record = {'phase': name, 'elapsed_seconds': round(time.monotonic() - started, 3),
                  'status': status, 'log_truncated': truncated}
        if build: record['vertices'] = list(vertices.values())
        evidence['runs'].append(record)
        print('validation_phase complete ' + json.dumps({k: v for k, v in record.items() if k != 'vertices'}), flush=True)
        (output / 'evidence.json').write_text(json.dumps(evidence, indent=2) + '\n')
        if status: raise subprocess.CalledProcessError(status, name)
        return record

    created = False
    functional_passed = False
    result = 1
    previous_signal = signal.signal(signal.SIGTERM, interrupted)
    try:
        # Creation can succeed just before its client is interrupted; the unique
        # name is safe to clean up even when the create call has no success reply.
        created = True
        run('create-builder', ['docker', 'buildx', 'create', '--name', builder, '--driver', 'docker-container'], 120)
        run('bootstrap-builder', ['docker', 'buildx', 'inspect', '--builder', builder, '--bootstrap'], 180)
        for mode in ('cold', 'warm'):
            clean_source(root, source_commit)
            dependencies = ['docker', 'buildx', 'build', '--builder', builder, '--progress=plain',
                            '--build-arg', 'MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=420',
                            '--target', 'dependency-cache', '--output', 'type=cacheonly']
            if mode == 'cold': dependencies.append('--no-cache')
            dependency_record = run(mode + '-dependencies', [*dependencies, '.'], 780, build=True)
            if mode == 'warm' and sum(v['cached'] and 'RUN --mount' in v['name']
                                      for v in dependency_record['vertices']) != 2:
                raise ValueError('Warm dependency phases did not reuse their validated cache')
            clean_source(root, source_commit)
            suffix = 'issue223-' + token + '-' + mode
            image = 'miningcore-source-test:' + suffix
            command = ['docker', 'buildx', 'build', '--builder', builder, '--progress=plain',
                       '--build-arg', 'MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=420', '--load', '--tag', image]
            build_record = run(mode + '-image', [*command, '.'], 1200, build=True)
            clean_source(root, source_commit)
            probe_record = run(mode + '-probes', ['docker', 'buildx', 'build', '--builder', builder, '--progress=plain',
                               '--build-arg', 'MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS=420',
                               '--target', 'runtime-probes', '--output', 'type=local,dest=' + str(exported), '.'], 300, build=True)
            # Cold probes must also reuse this job's audited publish. Their
            # five-minute deadline cannot accommodate another native build.
            require_publish_reuse(probe_record)
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
            environment = listener_environment(smoke, root, suffix)
            script_path = output / (mode + '-listeners.sh')
            script_path.write_text(smoke)
            run(mode + '-listeners', ['bash', str(script_path)], 300, environment)
            exported.rename(output / (mode + '-probes'))
        clean_source(root, source_commit)
        functional_passed = True
        result = 0
    except KeyboardInterrupt:
        evidence['interrupted'] = True
        result = 130
        print('Source-container validation interrupted', file=sys.stderr)
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print('Source-container validation failed: ' + str(error), file=sys.stderr)
    finally:
        signal.signal(signal.SIGTERM, previous_signal)
        cleanup_status = None
        if created:
            cleanup_status = remove_builder(builder)
            if cleanup_status != 0:
                print('Could not remove isolated lab builder: ' + builder, file=sys.stderr)
        result = finish(evidence, functional_passed, cleanup_status, result)
        (output / 'evidence.json').write_text(json.dumps(evidence, indent=2) + '\n')
    return result


if __name__ == '__main__':
    sys.exit(main())
