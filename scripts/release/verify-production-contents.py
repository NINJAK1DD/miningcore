#!/usr/bin/env python3
"""Reject lab tooling and generated test/private artifacts in production distributions.

Inspect tar headers without extraction. For Docker images inspect every saved layer,
including files hidden or deleted by later layers. This is a distribution policy,
not a replacement for secret scanning or a content-based malware scan.
"""
import argparse
import importlib.util
import json
from pathlib import Path, PurePosixPath
import posixpath
import re
import subprocess
import tarfile
import tempfile

spec = importlib.util.spec_from_file_location('production_secrets', Path(__file__).with_name('production-secrets.py'))
secrets = importlib.util.module_from_spec(spec)
spec.loader.exec_module(secrets)


def normalized(name):
    if not name or name.startswith('/') or '\\' in name or '..' in PurePosixPath(name).parts:
        raise ValueError(f'Unsafe archive path: {name!r}')
    return str(PurePosixPath(name))


def check_path(name, application):
    parts = PurePosixPath(name.casefold()).parts
    leaf = parts[-1] if parts else ''
    lab = any(p in {'test-lab', 'miningcore-test-lab', 'testresults', 'test-results', '.git'} or
              p.startswith(('miningcore-lab-', 'miningcore-gss-', 'miningcore-tcp-staging-',
                            'wallet-run-', 'miningcore.tests')) for p in parts)
    generated = leaf.endswith(('.trx', '.coverage', '.keytab', '.ccache', '.keys')) or leaf in {
        'wallet.dat', 'id_rsa', 'id_ed25519', '.env', 'coverage.cobertura.xml',
        'coverage.opencover.xml', 'junit.xml'} or leaf.startswith('.env.')
    # OS packages legitimately ship logs/caches. These additional restrictions
    # apply to the entire binary archive and to /app in container layers.
    app_generated = application and (any(p in {'logs', 'build', 'obj', '__pycache__', '.pytest_cache'} for p in parts) or
        leaf in {'config.json', 'pool.json', 'recovered-shares.txt'} or
        leaf.endswith(('.log', '.dump', '.bundle', '.pyc', '.pfx', '.p12', '.sqlite', '.db')))
    if lab or generated or app_generated:
        raise ValueError(f'Forbidden production artifact: {name!r}')


def check_member(member, image=False):
    name = normalized(member.name)
    application = not image or name == 'app' or name.startswith('app/')
    check_path(name, application)
    if member.issym() or member.islnk():
        target = member.linkname
        if '\\' in target or not target:
            raise ValueError(f'Unsafe archive link: {name!r}')
        if target.startswith('/'):
            if not image:
                raise ValueError(f'Absolute release link: {name!r}')
            resolved = target.lstrip('/')
        else:
            # Tar hardlink targets are archive-relative, symlinks are directory-relative.
            resolved = posixpath.normpath(posixpath.join(
                posixpath.dirname(name) if member.issym() else '', target))
        resolved = normalized(resolved)
        if not image and PurePosixPath(resolved).parts[0] != PurePosixPath(name).parts[0]:
            raise ValueError(f'Release link escapes package root: {name!r}')
        check_path(resolved, not image or resolved.startswith('app/'))
    return name


def verify_archive(path):
    count = 0
    app = False
    roots = set()
    with secrets.SecretScan() as scan, tarfile.open(path, 'r|gz') as archive:
        for member in archive:
            name = check_member(member)
            if member.isfile():
                with archive.extractfile(member) as content:
                    scan.add(content)
            if name != '.':
                roots.add(PurePosixPath(name).parts[0])
            app |= member.isfile() and len(PurePosixPath(name).parts) == 2 and name.endswith('/Miningcore')
            count += 1
            archive.members.clear()
    if not app or len(roots) != 1:
        raise ValueError('Expected one release root containing the Miningcore apphost')
    return count


def verify_saved_image(path):
    count = 0
    app = False
    with secrets.SecretScan() as scan, tarfile.open(path, 'r:') as saved:
        manifest = saved.getmember('manifest.json')
        if not manifest.isfile() or manifest.size > 1024 * 1024:
            raise ValueError('Invalid Docker image manifest')
        with saved.extractfile(manifest) as stream:
            images = json.load(stream)
        if not isinstance(images, list) or len(images) != 1 or not isinstance(images[0], dict):
            raise ValueError('Expected exactly one saved image')
        layers = images[0].get('Layers')
        if not isinstance(layers, list) or not layers or any(not isinstance(x, str) for x in layers):
            raise ValueError('Image manifest has no valid layers')
        scan.json(images)
        config = saved.getmember(normalized(images[0]['Config']))
        if not config.isfile() or config.size > 16 * 1024 * 1024:
            raise ValueError('Invalid Docker image configuration')
        with saved.extractfile(config) as content:
            scan.json(json.load(content))
        for layer in layers:
            entry = saved.getmember(normalized(layer))
            if not entry.isfile():
                raise ValueError('Image layer is not a regular file')
            with saved.extractfile(entry) as stream:
                # RFC 8878: ordinary and skippable frame signatures. Keep the
                # policy consistent on runners with different Python versions.
                magic = int.from_bytes(stream.read(4), 'little')
                if magic == 0xFD2FB528 or 0x184D2A50 <= magic <= 0x184D2A5F:
                    raise ValueError('Unsupported zstd Docker layer; export uncompressed or gzip layers for inspection')
                stream.seek(0)
                with tarfile.open(fileobj=stream, mode='r|*') as archive:
                    for member in archive:
                        name = check_member(member, image=True)
                        if member.isfile():
                            with archive.extractfile(member) as content:
                                scan.add(content)
                        app |= member.isfile() and name == 'app/Miningcore'
                        count += 1
                        archive.members.clear()
    if not app:
        raise ValueError('Saved image does not contain /app/Miningcore')
    return len(layers), count


def verify_image(reference, pull=False):
    if not reference or reference.startswith('-'):
        raise ValueError('Invalid image reference')
    if pull:
        if not re.fullmatch(r'[a-z0-9][a-z0-9._:/-]*@sha256:[0-9a-f]{64}', reference):
            raise ValueError('Registry inspection requires an immutable image@sha256 digest')
        # All production images are linux/amd64. Pull and save the same digest,
        # never a mutable staging tag or the previously checked preview image.
        subprocess.run(['docker', 'pull', '--platform', 'linux/amd64', reference], check=True, timeout=600)
    with tempfile.TemporaryDirectory(prefix='miningcore-content-check-') as directory:
        path = directory + '/image.tar'
        subprocess.run(['docker', 'image', 'save', '--output', path, reference], check=True, timeout=600)
        return verify_saved_image(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--archive', help='production .tar.gz release archive')
    group.add_argument('--image', help='locally built Docker image to inspect, without starting it')
    group.add_argument('--registry-image', help='pull and inspect an immutable image@sha256 digest (linux/amd64)')
    args = parser.parse_args()
    try:
        secrets.install()
        if args.archive:
            count = verify_archive(args.archive)
            print(f'Production archive contents passed: {count} entries')
        else:
            layers, count = verify_image(args.registry_image or args.image, pull=bool(args.registry_image))
            print(f'Production image contents passed: {layers} layers, {count} entries')
    except (OSError, ValueError, KeyError, TypeError, tarfile.TarError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Production contents check failed: {error}\n')


if __name__ == '__main__':
    main()
