#!/usr/bin/env python3
"""Run a command with five funded, isolated wallet families; retain private evidence."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True, help='Verified wallet installation directory')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if sys.platform != 'linux' or os.geteuid() == 0 or not command:
        parser.error('Run on Linux as an ordinary user and provide -- followed by the test command')
    root = args.root.resolve(strict=True)
    os.umask(0o077)
    run = Path(tempfile.mkdtemp(prefix='wallet-run-', dir=root))
    print(f'Private wallet evidence: {run}', flush=True)
    processes = []
    env = dict(os.environ, MININGCORE_WALLET_LAB_ROOT=str(root))
    try:
        for family in ('cryptonote', 'xelis', 'zano', 'zcash'):
            fixture = run / family
            with (run / (family + '.log')).open('w') as log:
                process = subprocess.Popen([sys.executable, str(Path(__file__).parent / 'wallet-fixtures' / (family + '.py'))],
                    env=dict(env, MININGCORE_WALLET_RUN=str(fixture)), stdout=log, stderr=subprocess.STDOUT,
                    start_new_session=True)
            processes.append((process, fixture))
        deadline = time.monotonic() + 600
        while not all((fixture / 'manifest.json').exists() for _, fixture in processes):
            if any(process.poll() is not None for process, _ in processes):
                raise RuntimeError(f'A wallet fixture exited; inspect private logs under {run}')
            if time.monotonic() >= deadline:
                raise TimeoutError(f'Wallet funding exceeded 10 minutes; inspect {run}')
            time.sleep(1)
        if any(process.poll() is not None for process, _ in processes):
            raise RuntimeError('Wallet fixture exited during readiness')
        manifest = {}
        for _, fixture in processes:
            manifest.update(json.loads((fixture / 'manifest.json').read_text()))
        if set(manifest) != {'monero', 'conceal', 'zano', 'zcash', 'xelis'}:
            raise RuntimeError('Incomplete wallet compatibility matrix')
        target = run / 'manifest.json'
        target.write_text(json.dumps(manifest, indent=2))
        env['MININGCORE_TEST_WALLET_MANIFEST'] = str(target)
        return subprocess.run(command, env=env, timeout=3 * 3600).returncode
    finally:
        for process, fixture in processes:
            if fixture.is_dir():
                (fixture / 'STOP').touch()
        for process, _ in processes:
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                pass
            # Each group was created above, so cleanup cannot target another lab.
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        for process, _ in processes:
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


if __name__ == '__main__':
    raise SystemExit(main())
