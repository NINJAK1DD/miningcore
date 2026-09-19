#!/usr/bin/env python3
"""Guard general native code generation; higher-ISA TUs require explicit review."""
import argparse
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
BASELINE = '-march=x86-64-v2 -mtune=generic -maes -mpclmul'
REVIEWED = {
    ('libdero/Makefile', 'include/highwayhash/hh_avx2.o: CXXFLAGS += -mavx2'),
}


def violations(name, content):
    errors = []
    for number, line in enumerate(content.splitlines(), 1):
        code = line.split('#', 1)[0].strip()
        if re.search(r'-march\s*=\s*native|-mtune\s*=\s*native|check_cpu\.sh|/proc/cpuinfo', code):
            errors.append(f'{name}:{number}: host-dependent code generation')
        if re.search(r'-m(?:avx|xop)|(?:-D|\b)HAVE_(?:AVX|XOP)', code) and (name, code) not in REVIEWED:
            errors.append(f'{name}:{number}: unreviewed optional-ISA flags')
    return errors


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        for value in ('CFLAGS += -march=native', 'CXXFLAGS += -mavx2',
                      'CFLAGS += -DHAVE_AVX512F', 'FLAGS := $(shell ./check_cpu.sh avx)',
                      'other.o: CXXFLAGS += -mavx2'):
            assert violations('libdero/Makefile', value), value
        assert not violations('libdero/Makefile', 'include/highwayhash/hh_avx2.o: CXXFLAGS += -mavx2')
        assert not violations('libother/Makefile', '# Do not use -march=native\nCFLAGS += $(CPU_FLAGS)')
        print('Native CPU policy negative fixtures passed')
        return
    errors = []
    makefiles = sorted((ROOT / 'src/Native').glob('*/Makefile'))
    assert makefiles, 'Native Makefiles missing'
    for path in makefiles:
        errors.extend(violations(path.relative_to(ROOT / 'src/Native').as_posix(), path.read_text()))
    driver = (ROOT / 'src/Miningcore/build-libs-linux.sh').read_text()
    assert f'export CPU_FLAGS="{BASELINE}"' in driver
    assert '-DARCH=default' in driver and '-DARCH=native' not in driver
    package = (ROOT / 'scripts/release/package-linux-x64.sh').read_text()
    assert f'Native CPU baseline: {BASELINE}' in package
    if errors:
        raise SystemExit('\n'.join(errors))
    print(f'Native CPU policy passed for all {len(makefiles)} top-level Makefiles and BUILD-INFO')


if __name__ == '__main__':
    main()
