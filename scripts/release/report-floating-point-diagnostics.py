#!/usr/bin/env python3
"""Emit runner-side warnings from captured numeric-fixture TRX output."""

import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

PREFIX = "Non-default floating-point controls at numeric fixture entry"


def diagnostics(path):
    root = ET.parse(path).getroot()
    for result in root.iter():
        if result.tag.rsplit('}', 1)[-1] != 'UnitTestResult':
            continue
        output = result.find('./{*}Output/{*}StdOut')
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
    except (OSError, ET.ParseError) as error:
        print(f'Cannot read floating-point diagnostic report: {error}', file=sys.stderr)
        return 1
    print(f'Floating-point diagnostics: {len(seen)} distinct test warning(s)')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
