#!/usr/bin/env python3
"""Emit runner-side warnings from captured numeric-fixture TRX output."""

import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

PREFIX = "Non-default floating-point controls at numeric fixture entry"
TRX_NAMESPACE = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
REQUIRED_FIXTURES = {
    ('Miningcore.Tests.VarDiffManagerTests', 'ExtremeRatios_AvoidIntermediateOverflowAndUnderflow'): 4,
    ('Miningcore.Tests.Mining.CryptonoteDifficultyAssignmentTests',
     'Configuration_RejectsFrozenDeltaAndIncompatibleNormalizedStart'): 1,
}


def read_report(path):
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
    fixtures = {fixture: set() for fixture in REQUIRED_FIXTURES}
    for definition in root.findall(f'./{namespace}TestDefinitions/{namespace}UnitTest'):
        method = definition.find(f'{namespace}TestMethod')
        if definition.get('id') in executed and method is not None:
            identity = method.get('className'), method.get('name')
            if identity in fixtures:
                fixtures[identity].add(definition.get('id'))
    incomplete = [f'{owner}.{method}: expected {expected} distinct executed case(s), found {len(fixtures[owner, method])}'
                  for (owner, method), expected in sorted(REQUIRED_FIXTURES.items())
                  if len(fixtures[owner, method]) != expected]
    items = []
    for result in results:
        output = result.find(f'./{namespace}Output/{namespace}StdOut')
        if output is None:
            continue
        for line in (output.text or '').splitlines():
            message = line.strip()
            if message.startswith(PREFIX):
                items.append((result.get('testName', '(unnamed test)'), message))
    # Coverage failures must not discard contamination evidence from a partial run.
    return items, incomplete


def workflow_warning(test_name, message):
    data = f'{test_name}: {message}'
    data = data.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    return f'::warning title=Non-default floating-point controls::{data}'


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('reports', type=Path, nargs='+')
    args = parser.parse_args(argv)
    seen = set()
    errors = []
    for report in args.reports:
        try:
            items, incomplete = read_report(report)
        except (OSError, ET.ParseError, ValueError) as error:
            errors.append(str(error))
            continue
        for item in items:
            if item not in seen:
                seen.add(item)
                print(workflow_warning(*item), flush=True)
        if incomplete:
            errors.append(f'{report}: incomplete numeric fixture evidence: ' + '; '.join(incomplete))
    for error in errors:
        print(f'Cannot read floating-point diagnostic report: {error}', file=sys.stderr)
    if errors:
        return 1
    print(f'Floating-point diagnostics: {len(seen)} distinct test warning(s)')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
