#!/usr/bin/env python3
"""Keep stdin-compatible cumulative SQL installers in sync with the canonical migration."""
import argparse
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--check", action="store_true")
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
scripts = root / "src/Miningcore/Persistence/Postgres/Scripts"
marker = "-- BEGIN GENERATED PPS ARITHMETIC MIGRATION"
legacy = "-- Stop all share producers/relays and drain recovery journals before upgrading."
canonical = (scripts / "add_pps_arithmetic_version.sql").read_text().replace("\\set ON_ERROR_STOP on\n", "")
for name in ("createdb.sql", "add_share_accounting.sql"):
    path = scripts / name
    original = path.read_text()
    prefix = original.split(marker)[0].split(legacy)[0].rstrip()
    expected = prefix + "\n\n" + marker + "\n-- Source: add_pps_arithmetic_version.sql; run scripts/release/sync-pps-arithmetic-migration.py\n" + canonical
    if args.check:
        if original != expected:
            raise SystemExit(f"{name}: generated arithmetic migration is stale")
    else:
        path.write_text(expected, newline="\n")
