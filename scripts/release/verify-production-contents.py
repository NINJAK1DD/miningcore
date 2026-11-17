#!/usr/bin/env python3
"""Reject lab tooling and generated test/private artifacts in production distributions.

Inspect tar headers without extraction. For Docker images inspect every saved layer,
including files hidden or deleted by later layers. This is a distribution policy,
not a replacement for secret scanning or a content-based malware scan.
"""
import argparse
import json
from pathlib import PurePosixPath
import posixpath
import subprocess
import tarfile
import tempfile


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
    with tarfile.open(path, 'r|gz') as archive:
        for member in archive:
            name = check_member(member)
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
    with tarfile.open(path, 'r:') as saved:
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
        for layer in layers:
            entry = saved.getmember(normalized(layer))
            if not entry.isfile():
                raise ValueError('Image layer is not a regular file')
            with saved.extractfile(entry) as stream, tarfile.open(fileobj=stream, mode='r|*') as archive:
                for member in archive:
                    name = check_member(member, image=True)
                    app |= member.isfile() and name == 'app/Miningcore'
                    count += 1
                    archive.members.clear()
    if not app:
        raise ValueError('Saved image does not contain /app/Miningcore')
    return len(layers), count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument('--archive', help='production .tar.gz release archive')
    group.add_argument('--image', help='locally built Docker image to inspect, without starting it')
    args = parser.parse_args()
    try:
        if args.archive:
            count = verify_archive(args.archive)
            print(f'Production archive contents passed: {count} entries')
        else:
            if not args.image or args.image.startswith('-'):
                raise ValueError('Invalid image reference')
            with tempfile.TemporaryDirectory(prefix='miningcore-content-check-') as directory:
                path = directory + '/image.tar'
                subprocess.run(['docker', 'image', 'save', '--output', path, args.image],
                               check=True, timeout=600)
                layers, count = verify_saved_image(path)
                print(f'Production image contents passed: {layers} layers, {count} entries')
    except (OSError, ValueError, KeyError, TypeError, tarfile.TarError, subprocess.SubprocessError) as error:
        parser.exit(1, f'Production contents check failed: {error}\n')


if __name__ == '__main__':
    main()
