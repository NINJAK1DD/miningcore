#!/usr/bin/env python3
"""Pinned, offline-at-scan-time secret detection for production payloads.

Call install() explicitly to acquire the reviewed scanner. No candidate-controlled
configuration, ignore file, filename exclusions or inline allow comments apply.
"""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import subprocess
from subprocess import run as run_scanner
import tarfile
import tempfile
import urllib.request
import zipfile

VERSION = '8.30.1'
PINS = {
    'Linux': ('linux_x64.tar.gz', '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb',
              'gitleaks', '88f91962aa2f93ac6ab281d553b9e125f5197bbbce38f9f2437f7299c32e5509'),
    'Windows': ('windows_x64.zip', 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e',
                'gitleaks.exe', '17157e2ee8b76fc8b1d8bee607a250e34b8a8023c8bc81822d4b5ee4d78fcb7c'),
}
ROOT = Path(__file__).resolve().parents[2]


def scanner():
    if platform.machine().lower() not in ('amd64', 'x86_64') or platform.system() not in PINS:
        raise ValueError('Production secret scanner requires a qualified x64 Linux or Windows host')
    _, _, filename, digest = PINS[platform.system()]
    path = ROOT / 'build/release-tools/gitleaks' / filename
    if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != digest:
        raise ValueError('Missing or changed secret scanner; run python3 scripts/release/production-secrets.py --install')
    return path


def install():
    try:
        return scanner()
    except ValueError:
        pass
    if platform.machine().lower() not in ('amd64', 'x86_64') or platform.system() not in PINS:
        raise ValueError('Unsupported scanner platform')
    suffix, archive_hash, filename, binary_hash = PINS[platform.system()]
    url = f'https://github.com/gitleaks/gitleaks/releases/download/v{VERSION}/gitleaks_{VERSION}_{suffix}'
    with urllib.request.urlopen(url, timeout=120) as response:
        data = response.read(16 * 1024 * 1024 + 1)
    if hashlib.sha256(data).hexdigest() != archive_hash:
        raise ValueError('Secret scanner archive checksum mismatch')
    if suffix.endswith('.zip'):
        with zipfile.ZipFile(io.BytesIO(data)) as package:
            binary = package.read(filename)
    else:
        with tarfile.open(fileobj=io.BytesIO(data), mode='r:gz') as package:
            binary = package.extractfile(filename).read()
    if hashlib.sha256(binary).hexdigest() != binary_hash:
        raise ValueError('Secret scanner binary checksum mismatch')
    path = ROOT / 'build/release-tools/gitleaks' / filename
    path.parent.mkdir(parents=True, exist_ok=True)
    # The containing build directory is private to the build user. Never extract
    # archive-controlled paths, links or permissions into the workspace.
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as temporary:
        temporary.write(binary)
        temporary_path = Path(temporary.name)
    try:
        temporary_path.chmod(0o700)
        temporary_path.replace(path)
    finally:
        temporary_path.unlink(missing_ok=True)
    return scanner()


class SecretScan:
    def __enter__(self):
        self.executable = scanner()
        self.directory = tempfile.TemporaryDirectory(prefix='miningcore-secret-check-')
        self.path = Path(self.directory.name)
        self.stream = (self.path / 'payload').open('w+b')
        # Feed stdin with a text prefix, including for binary payloads: Gitleaks
        # otherwise skips application/octet-stream before inspecting its strings.
        self.stream.write(b'Miningcore production payload strings\n')
        self.bytes = 0
        return self

    def add(self, source):
        previous = b''
        while chunk := source.read(64 * 1024):
            self.bytes += len(chunk)
            if self.bytes > 8 * 1024**3:
                raise ValueError('Secret scan exceeds 8 GiB uncompressed input budget; inspection is incomplete')
            data = previous + chunk
            # Preserve original bytes and also inspect embedded UTF-16 ASCII
            # strings. Overlap prevents a credential at a chunk boundary escaping.
            self.stream.write(b'\n' + data + b'\n')
            if b'\0' in data:
                self.stream.write(data.replace(b'\0', b'') + b'\n')
            previous = data[-8192:]

    def json(self, value):
        self.add(io.BytesIO(json.dumps(value, ensure_ascii=False).encode()))
        def strings(item):
            if isinstance(item, str):
                self.add(io.BytesIO(item.encode()))
            elif isinstance(item, dict):
                for key, child in item.items():
                    strings(key)
                    strings(child)
            elif isinstance(item, list):
                for child in item:
                    strings(child)
        strings(value)

    def __exit__(self, kind, value, traceback):
        try:
            if kind is None:
                self.stream.seek(0)
                config = self.path / 'scanner.toml'
                config.write_text('[extend]\nuseDefault = true\n', encoding='utf-8')
                ignore = self.path / 'empty.ignore'
                ignore.touch()
                # No snippets, credential values or candidate paths enter CI logs.
                result = run_scanner([str(self.executable), 'stdin', '--config', str(config),
                    '--gitleaks-ignore-path', str(ignore), '--ignore-gitleaks-allow',
                    '--max-target-megabytes', '0', '--max-decode-depth', '3', '--redact=100',
                    '--no-banner', '--log-level', 'error'], stdin=self.stream,
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=600,
                    cwd=self.path, env={key: val for key, val in os.environ.items()
                                       if not key.startswith('GITLEAKS_')})
                if result.returncode:
                    raise ValueError('Production secret scan rejected content or could not complete; inspect privately with the pinned scanner')
        finally:
            self.stream.close()
            self.directory.cleanup()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--install', action='store_true', required=True)
    parser.parse_args()
    install()
    print(f'Checksum-verified Gitleaks {VERSION} installed')
