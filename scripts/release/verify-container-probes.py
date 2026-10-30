#!/usr/bin/env python3
"""Fail closed unless the probe builder and actual runtime image have equal code."""
import json
from pathlib import Path
import re
import sys


def verify(manifest, checksums):
    evidence = json.loads(manifest)
    if evidence.get('schema') != 1 or evidence.get('complete') is not True:
        raise ValueError('Incomplete probe evidence')
    natives = evidence.get('native_libraries')
    if not isinstance(natives, dict) or not 1 <= len(natives) <= 64:
        raise ValueError('Missing or excessive native libraries')
    if any(not re.fullmatch(r'[A-Za-z0-9_.+-]+\.so', name) for name in natives):
        raise ValueError('Invalid native library name')
    expected = {'Miningcore.dll': evidence.get('application_sha256'), **natives}
    if any(not isinstance(value, str) or not re.fullmatch('[0-9a-f]{64}', value) for value in expected.values()):
        raise ValueError('Invalid artifact digest')
    actual = {}
    for line in checksums.splitlines():
        match = re.fullmatch(r'([0-9a-f]{64})  (?:\./)?([A-Za-z0-9_.+-]+)', line)
        if not match or match[2] in actual:
            raise ValueError('Malformed or duplicate image checksum')
        actual[match[2]] = match[1]
    if actual != expected:
        raise ValueError('Runtime image differs from the audited probe builder')


def main():
    try:
        with Path(sys.argv[1]).open() as stream:
            manifest = stream.read(16385)
        checksums = sys.stdin.read(16385)
        if len(manifest) > 16384 or len(checksums) > 16384:
            raise ValueError('Artifact evidence exceeds its size limit')
        verify(manifest, checksums)
    except (OSError, ValueError, TypeError, KeyError, IndexError) as error:
        print('Container provenance check failed: ' + str(error), file=sys.stderr)
        return 1
    print('Runtime image matches the audited application and native libraries')
    return 0


if __name__ == '__main__':
    sys.exit(main())
