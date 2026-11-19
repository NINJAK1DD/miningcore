#!/usr/bin/env bash
# Emit one nonempty ASCII ERE, with or without a final LF in the source.
set -eu
contract="$(dirname "${BASH_SOURCE[0]}")/release-version.ere"
LC_ALL=C awk 'NR == 1 && /^[!-~]+$/ { pattern = $0 }
  END { if (NR != 1 || !length(pattern)) exit 1; print pattern }' "$contract" || {
  echo "Cannot load $contract: expected one nonempty line of printable ASCII without whitespace" >&2
  exit 1
}
