#!/usr/bin/env python3
"""Build managed runtime probes against this builder's already audited application."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
import time


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def publish(root, application, output):
    root, application, output = root.resolve(), application.resolve(), output.resolve()
    built = root / 'src/Miningcore/bin/Release/net10.0/Miningcore.dll'
    published = application / 'Miningcore.dll'
    if output.exists():
        raise ValueError('Probe export must be a new directory')
    if digest(built) != digest(published):
        raise ValueError('Referenced application does not match the audited publish')
    libraries = sorted(application.glob('*.so'))
    if not libraries or len(libraries) > 64:
        raise ValueError('Audited application native libraries are missing')
    budget = int(os.environ.get('MININGCORE_PROBE_TIMEOUT_SECONDS', '180'))
    if not 1 <= budget <= 600:
        raise ValueError('Invalid probe build budget')
    output.mkdir(parents=True)
    evidence = {'schema': 1, 'application_sha256': digest(published),
                'native_libraries': {path.name: digest(path) for path in libraries},
                'platform': platform.machine(), 'phases': [], 'complete': False}
    result = 0
    deadline = time.monotonic() + budget
    try:
        with tempfile.TemporaryDirectory(prefix='container-probe-audit-') as temporary:
            audit = root / 'scripts/release/run-warning-audited-dotnet.sh'

            def run(name, arguments, audited=False):
                log = Path(temporary) / (name + '.log')
                log.touch()
                command = ['bash', str(audit), str(log), *arguments] if audited else ['dotnet', *arguments]
                started = time.monotonic()
                remaining = deadline - started
                if remaining <= 0:
                    status = 124
                else:
                    completed = subprocess.run(['timeout', '--signal=TERM', '--kill-after=10s', f'{remaining:.3f}s', *command],
                                               cwd=root, env=dict(os.environ, MININGCORE_ALLOW_BUILD_WARNINGS='0'))
                    status = completed.returncode if completed.returncode >= 0 else 128 - completed.returncode
                phase = {'name': name, 'elapsed_seconds': round(time.monotonic() - started, 3), 'status': status}
                evidence['phases'].append(phase)
                print('container_probe_phase ' + json.dumps(phase, separators=(',', ':')), flush=True)
                if status:
                    raise subprocess.CalledProcessError(status, name)

            host = 'src/Miningcore.Tests.ProcessHost/Miningcore.Tests.ProcessHost.csproj'
            options = ['--configuration', 'Release', '--framework', 'net10.0']
            run('restore-listener-host', ['restore', host])
            # --no-dependencies is a build option: the referenced Miningcore and
            # native outputs must already exist in this same Docker builder.
            run('build-listener-host', ['build', host, *options, '--no-restore', '--no-dependencies'], True)
            run('publish-listener-host', ['publish', host, *options, '--no-build', '--no-restore',
                                         '--output', str(output / 'api-listener-smoke-host')], True)
            if digest(output / 'api-listener-smoke-host/Miningcore.dll') != evidence['application_sha256']:
                raise ValueError('Listener host changed the audited application assembly')
            reject_probe_natives(output)
            run('publish-zeromq-probe', ['publish', 'scripts/release/fixtures/zeromq-runtime-probe/ZeroMqRuntimeProbe.csproj',
                                       *options, '--output', str(output / 'zeromq-runtime-probe')], True)
            reject_probe_natives(output)
            evidence['complete'] = True
    except subprocess.CalledProcessError as error:
        result = error.returncode
    except (OSError, ValueError) as error:
        print('Container probe validation failed: ' + str(error), file=sys.stderr)
        result = 70
    finally:
        # Fixed schema and four phases; never serialize environment or tool output.
        (output / 'build-evidence.json').write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    return result


def reject_probe_natives(output):
    # Probes must resolve native code from the audited /app via LD_LIBRARY_PATH.
    # Include versioned objects and nested publish directories in this boundary.
    if any(path.name.endswith('.so') or '.so.' in path.name
           for path in output.rglob('*')):
        raise ValueError('Probe output must not contain native shared libraries')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=Path)
    parser.add_argument('application', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    try:
        return publish(args.root, args.application, args.output)
    except (OSError, ValueError) as error:
        print('Container probe prerequisites failed: ' + str(error), file=sys.stderr)
        return 70


if __name__ == '__main__':
    sys.exit(main())
