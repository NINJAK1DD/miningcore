#!/usr/bin/env bash
# Emit one nonempty ASCII ERE, with or without a final LF in the source.
# An optional caller prefix applies only to the failure diagnostic on stderr.
set -eu
contract="$(dirname "${BASH_SOURCE[0]}")/release-version.ere"
LC_ALL=C awk 'NR == 1 && /^[!-~]+$/ { pattern = $0 }
  END { if (NR != 1 || !length(pattern)) exit 1; print pattern }' "$contract" || {
  printf '%sCannot load %s: expected one nonempty line of printable ASCII without whitespace\n' "${1:-}" "$contract" >&2
  exit 1
}
