#!/usr/bin/env bash
set -euo pipefail

if (( $# > 1 )) || [[ ${1-Debug} != Debug && ${1-Debug} != Release ]]; then
    echo 'Usage: bash scripts/release/update-config-diagnostics-snapshot.sh [Debug|Release]' >&2
    exit 1
fi
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)

# Python 3 is already a source-build/CI dependency. Capture the process output
# without shell interpolation and validate it before replacing the fixture.
python3 - "$root" "${1-Debug}" <<'PY'
import json
import subprocess
import sys
from pathlib import Path

root = Path(sys.argv[1])
assembly = root / f"src/Miningcore/bin/{sys.argv[2]}/net10.0/Miningcore.dll"
example = root / "config.example.json"
snapshot = root / "src/Miningcore.Tests/Fixtures/config-diagnostics-v2.json"
if not assembly.is_file():
    sys.exit("Build Miningcore in the requested configuration before regenerating the snapshot.")

# Only the checked-in public example is accepted, never a caller-supplied path.
try:
    result = subprocess.run(
        ["dotnet", str(assembly), "--dumpconfig", "-c", str(example)],
        capture_output=True, check=False, timeout=60,
    )
except (OSError, subprocess.TimeoutExpired):
    sys.exit("Configuration diagnostics could not run; the snapshot was not changed.")
if result.returncode != 0:
    sys.exit("Configuration diagnostics failed; the snapshot was not changed.")
try:
    text = result.stdout.decode("utf-8-sig").replace("\r\n", "\n").replace("\r", "\n")
    document = json.loads(text)
except (UnicodeError, ValueError):
    sys.exit("Invalid diagnostic JSON; the snapshot was not changed.")
if (not isinstance(document, dict)
        or type(document.get("diagnosticFormatVersion")) is not int
        or document["diagnosticFormatVersion"] != 2
        or not isinstance(document.get("configuration"), dict)):
    sys.exit("Unexpected diagnostic contract; review the format before replacing the snapshot.")
snapshot.write_bytes((text.rstrip("\n") + "\n").encode("utf-8"))
print("Updated diagnostic snapshot. Review the fixture diff before committing.")
PY
