#!/usr/bin/env python3
"""Emit runner-side warnings from captured numeric-fixture TRX output."""

import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

PREFIX = "Non-default floating-point controls at numeric fixture entry"
TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
REQUIRED_FIXTURES = {
    ('Miningcore.Tests.VarDiffManagerTests', 'ExtremeRatios_AvoidIntermediateOverflowAndUnderflow'),
    ('Miningcore.Tests.Mining.CryptonoteDifficultyAssignmentTests',
     'Configuration_RejectsFrozenDeltaAndIncompatibleNormalizedStart'),
}


def diagnostics(path):
    root = ET.parse(path).getroot()
    namespace = f'{{{TRX_NAMESPACE}}}' if root.tag == f'{{{TRX_NAMESPACE}}}TestRun' else ''
    if root.tag != namespace + 'TestRun':
        raise ValueError(f'{path}: expected a TRX TestRun root')
    results = root.findall(f'./{namespace}Results/{namespace}UnitTestResult')
    if not results:
        raise ValueError(f'{path}: expected at least one TRX UnitTestResult in Results')
    # Match canonical class/method identities, not xUnit's formatted theory names.
    # Failed tests still carry useful diagnostics; skipped/discovered-only tests
    # do not establish that either protected numeric path actually ran.
    executed = {result.get('testId') for result in results
                if result.get('testId') and result.get('outcome') in ('Passed', 'Failed')}
    fixtures = set()
    for definition in root.findall(f'./{namespace}TestDefinitions/{namespace}UnitTest'):
        method = definition.find(f'{namespace}TestMethod')
        if definition.get('id') in executed and method is not None:
            fixtures.add((method.get('className'), method.get('name')))
    missing = REQUIRED_FIXTURES - fixtures
    if missing:
        names = ', '.join(f'{owner}.{method}' for owner, method in sorted(missing))
        raise ValueError(f'{path}: missing executed numeric fixtures: {names}')
    for result in results:
        output = result.find(f'./{namespace}Output/{namespace}StdOut')
        if output is None:
            continue
        for line in (output.text or '').splitlines():
            message = line.strip()
            if message.startswith(PREFIX):
                yield result.get('testName', '(unnamed test)'), message


def workflow_warning(test_name, message):
    data = f'{test_name}: {message}'
    data = data.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    return f'::warning title=Non-default floating-point controls::{data}'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reports', type=Path, nargs='+')
    args = parser.parse_args(argv)
    seen = set()
    try:
        for report in args.reports:
            for item in diagnostics(report):
                if item not in seen:
                    seen.add(item)
                    print(workflow_warning(*item))
    except (OSError, ET.ParseError, ValueError) as error:
        print(f'Cannot read floating-point diagnostic report: {error}', file=sys.stderr)
        return 1
    print(f'Floating-point diagnostics: {len(seen)} distinct test warning(s)')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
