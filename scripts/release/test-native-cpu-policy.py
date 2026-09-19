#!/usr/bin/env python3
"""Review guard for native build files, not a sandbox for arbitrary build code."""
import argparse
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
BASELINE = '-march=x86-64-v2 -mtune=generic -maes -mpclmul'
# Architecture/tuning selection belongs exclusively to the driver.
BASELINE_FLAGS = {'-m64', '-maes', '-mpclmul', '-msse4.1'}
REVIEWED = {
    ('libdero/Makefile', 'include/highwayhash/hh_avx2.o: CXXFLAGS += -mavx2'),
}
# uname's -m prints the machine name; it is not a compiler ISA switch.
FALSE_POSITIVE_ALLOWANCES = {
    ('libdero/Makefile', 'UNAME_P ?= $(shell uname -m)'),
}
BASELINE_FEATURES = {'HAVE_SSE2', 'HAVE_SSE3', 'HAVE_SSSE3', 'HAVE_PCLMUL', 'HAVE_AES'}
BUILD_NAMES = {'gnumakefile', 'makefile', 'cmakelists.txt'}


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def violations(name, content):
    errors = []
    # A continued flag must not inherit a target-specific rule's exemption.
    content = re.sub(r'\\\r?\n', ' ', content)
    for number, line in enumerate(content.splitlines(), 1):
        code = line.split('#', 1)[0].strip()
        if re.search(r'check_cpu\.sh|/proc/cpuinfo', code):
            errors.append(f'{name}:{number}: host-dependent code generation')
        if re.search(r'\$[({]MAKE[)}]|\b(?:make|cmake)\s|^(?:-?include|sinclude)\s', code):
            errors.append(f'{name}:{number}: unreviewed nested build invocation/include')
        if (name, code) in REVIEWED | FALSE_POSITIVE_ALLOWANCES:
            continue
        # Capture incomplete/dynamic -m switches too: splitting a value with
        # whitespace, quoting or a Make expansion fails closed.
        for flag in re.findall(r'(?<![\w])-m[^\s\'"();\\]*', code):
            if flag not in BASELINE_FLAGS:
                errors.append(f'{name}:{number}: unreviewed machine flag {flag}')
        for feature in re.findall(r'(?:-D|\b)(HAVE_[A-Z0-9_]+)', code):
            if feature not in BASELINE_FEATURES and feature != 'HAVE_FEATURE':
                errors.append(f'{name}:{number}: unreviewed feature macro {feature}')
        if re.search(r'/arch:|(?<![\w-])(?:-target\b|-Xclang\b)|__(?:AVX|FMA|BMI|SHA|VAES|XOP)\w*__', code):
            errors.append(f'{name}:{number}: unreviewed compiler target override')
    return errors


def check_tree(native, inactive):
    errors = []
    files = sorted(p for p in native.rglob('*') if p.is_file() and p.name.lower() in BUILD_NAMES)
    require(files, 'Native build files missing')
    seen = set()
    active = 0
    for path in files:
        name = path.relative_to(native).as_posix()
        content = path.read_text(encoding='utf-8')
        top_level = len(path.relative_to(native).parts) == 2
        if top_level and path.name.lower() in {'gnumakefile', 'makefile'} and path.name != 'Makefile':
            errors.append(f'{name}: shadow Makefile is forbidden, including inactive allowances')
        elif top_level and path.name == 'Makefile':
            active += 1
            errors.extend(violations(name, content))
        else:
            seen.add(name)
            entry = inactive.get(name)
            if not entry or not entry.get('reason'):
                errors.append(f'{name}: nested build file requires explicit inactive-file review')
            elif hashlib.sha256(content.encode()).hexdigest() != entry.get('sha256'):
                errors.append(f'{name}: inactive build file changed; review its flags and reachability')
    require(active, 'Top-level native Makefiles missing')
    for name in inactive.keys() - seen:
        errors.append(f'{name}: stale inactive build-file allowance')
    return errors, active, len(seen)


def check_contract(driver, package):
    require(f'export CPU_FLAGS="{BASELINE}"' in driver, 'Driver CPU baseline changed')
    require('-DARCH=default' in driver and '-DARCH=native' not in driver, 'RandomX ARCH policy changed')
    require(f'Native CPU baseline: {BASELINE}' in package, 'BUILD-INFO CPU baseline changed')
    invocations = re.findall(r'^\s*make\b[^\n]*', driver, re.MULTILINE)
    require(invocations and all(re.match(r'\s*make -f Makefile(?:\s|$)', line) for line in invocations),
            'Every native Make invocation must explicitly select -f Makefile')


def update_manifest(native, inactive, names):
    """Refresh only explicitly selected, already reviewed entries; validate before writing."""
    updated = {name: dict(entry) for name, entry in inactive.items()}
    for name in names:
        path = native / name
        require(name in inactive and inactive[name].get('reason', '').strip(),
                f'{name}: add an explicit review reason to the manifest first')
        require(not Path(name).is_absolute() and '..' not in Path(name).parts and
                path.resolve().is_relative_to(native.resolve()) and path.is_file(),
                f'{name}: expected an existing file inside src/Native')
        updated[name]['sha256'] = hashlib.sha256(path.read_text(encoding='utf-8').encode()).hexdigest()
    errors, _, _ = check_tree(native, updated)
    require(not errors, '\n'.join(errors))
    return updated


def self_test():
    negative = ('-march=native', '-march=x86-64-v2', '-march=x86-64-v3',
                '-march=x86-64-v4', '-march=haswell', '-march=skylake-avx512',
                '-mtune=generic', '-mtune=native', '-mavx2', '-mfma', '-mbmi',
                '-mbmi2', '-msha', '-mvaes', '-mfuture', '-march = haswell',
                '-march="haswell"', '-m$(ISA)', '-DHAVE_AVX512F', '-DHAVE_FMA',
                '-D__AVX2__', '/arch:AVX2', '-Xclang -target-feature +avx2')
    for flag in negative:
        require(violations('libdero/Makefile', f'CXXFLAGS += {flag}'), f'Missed unsafe flag: {flag}')
    for code in ('FLAGS := $(shell ./check_cpu.sh avx)', 'other.o: CXXFLAGS += -mavx2',
                 'include/highwayhash/hh_avx2.o: CXXFLAGS += -mavx2 -mfma',
                 'include/highwayhash/hh_avx2.o: CXXFLAGS += -mavx2 ' + '\\\n -mfma',
                 '\t$(MAKE) -C blake3', '\tcmake -S xmrig -B build', 'include nested.mk'):
        require(violations('libdero/Makefile', code), f'Missed unsafe rule: {code}')
    require(violations('other/Makefile', next(iter(REVIEWED))[1]), 'Allowance escaped its file')
    for name, code in REVIEWED | FALSE_POSITIVE_ALLOWANCES:
        require(not violations(name, code), 'Reviewed target rejected')
    require(not violations('libother/Makefile', '# -march=native\nCFLAGS += $(CPU_FLAGS) -maes -m64'),
            'Baseline flags/comment rejected')
    # Test recursive discovery and content pinning, also under python -O.
    import tempfile
    with tempfile.TemporaryDirectory() as temporary:
        native = Path(temporary)
        nested = native / 'lib/nested/makefile'
        nested.parent.mkdir(parents=True)
        (native / 'lib/Makefile').write_text('CFLAGS += $(CPU_FLAGS)\n')
        nested.write_text('CFLAGS += -mavx2\n')
        entry = {'lib/nested/makefile': {'reason': 'unused fixture',
                 'sha256': hashlib.sha256(nested.read_text().encode()).hexdigest()}}
        require(not check_tree(native, entry)[0], 'Pinned inactive fixture rejected')
        nested.write_text('CFLAGS += -mfma\n')
        require(check_tree(native, entry)[0], 'Nested file drift accepted')
        updated = update_manifest(native, entry, [nested.relative_to(native).as_posix()])
        require(not check_tree(native, updated)[0], 'Reviewed hash refresh failed')
        require(update_manifest(native, updated, list(updated)) == updated, 'Refresh is not idempotent')
        require(check_tree(native, {})[0], 'Unreviewed nested file accepted')
        for shadow_name in ('GNUmakefile', 'makefile'):
            shadow = native / 'lib' / shadow_name
            # On Windows, lowercase makefile aliases the active Makefile.
            if shadow.exists():
                continue
            shadow.write_text('CFLAGS += -mavx512f\n')
            shadow_entry = {**updated, f'lib/{shadow_name}': {'reason': 'cannot exempt shadow',
                'sha256': hashlib.sha256(shadow.read_text().encode()).hexdigest()}}
            require(check_tree(native, shadow_entry)[0], 'Pinned shadow Makefile accepted')
            try:
                update_manifest(native, shadow_entry, [f'lib/{shadow_name}'])
            except SystemExit:
                pass
            else:
                raise SystemExit('Manifest refresh approved a shadow Makefile')
            shadow.unlink()
        unknown = native / 'lib/nested/GNUmakefile'
        unknown.write_text('CFLAGS += -mavx512f\n')
        require(check_tree(native, updated)[0], 'Nested GNUmakefile escaped discovery')
        for names in ([unknown.relative_to(native).as_posix()], ['../outside'], list(updated)):
            try:
                update_manifest(native, updated, names)
            except SystemExit:
                pass
            else:
                raise SystemExit('Refresh accepted unreviewed files or unrelated tree violations')
        unknown.unlink()
        nested.unlink()
        require(check_tree(native, entry)[0], 'Stale allowance accepted')
    valid_driver = f'export CPU_FLAGS="{BASELINE}"\ncmake -DARCH=default\nmake -f Makefile clean\nmake -f Makefile "$@"'
    valid_package = f'Native CPU baseline: {BASELINE}'
    check_contract(valid_driver, valid_package)
    for call in ('make clean', 'make "$@"', 'make'):
        try:
            check_contract(valid_driver + '\n  ' + call, valid_package)
        except SystemExit:
            continue
        raise SystemExit('Implicit Makefile selection accepted')
    for driver, package in (('', f'Native CPU baseline: {BASELINE}'),
                            (f'export CPU_FLAGS="{BASELINE}"', ''),
                            (f'export CPU_FLAGS="{BASELINE}" -DARCH=default', '')):
        try:
            check_contract(driver, package)
        except SystemExit:
            continue
        raise SystemExit('Build contract drift accepted')
    print('Native CPU policy negative fixtures passed')


def main():
    parser = argparse.ArgumentParser()
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--self-test', action='store_true')
    mode.add_argument('--update', nargs='+', metavar='NATIVE_RELATIVE_PATH',
                        help='refresh hashes only for these explicitly reviewed manifest entries')
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    manifest = Path(__file__).with_name('native-inactive-build-files.json')
    require(manifest.is_file(), 'Inactive build-file manifest missing')
    inactive = json.loads(manifest.read_text(encoding='utf-8'))
    if args.update:
        check_contract((ROOT / 'src/Miningcore/build-libs-linux.sh').read_text(),
                       (ROOT / 'scripts/release/package-linux-x64.sh').read_text())
        inactive = update_manifest(ROOT / 'src/Native', inactive, args.update)
        manifest.write_text(json.dumps(inactive, indent=2) + '\n', encoding='utf-8')
        print('Refreshed reviewed entries: ' + ', '.join(args.update))
    errors, active, nested = check_tree(ROOT / 'src/Native', inactive)
    check_contract((ROOT / 'src/Miningcore/build-libs-linux.sh').read_text(),
                   (ROOT / 'scripts/release/package-linux-x64.sh').read_text())
    require(not errors, '\n'.join(errors))
    print(f'Native CPU policy passed: {active} active Makefiles, {nested} pinned inactive build files and BUILD-INFO')


if __name__ == '__main__':
    main()
