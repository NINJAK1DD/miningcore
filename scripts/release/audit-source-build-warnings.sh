#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 BUILD_LOG" >&2
  exit 64
fi

case ${MININGCORE_ALLOW_BUILD_WARNINGS:-0} in
  0|1) ;;
  *)
    echo "MININGCORE_ALLOW_BUILD_WARNINGS must be either 0 or 1" >&2
    exit 64
    ;;
esac

audit=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/assert-warning-free-build.sh

set +e
bash "$audit" "$1"
status=$?
set -e

if [[ "$status" -eq 0 ]]; then
  exit 0
fi

# Only an ordinary warning finding may be overridden. Structural failures such
# as an unreadable private log remain fatal so the source build cannot fail open.
if [[ "$status" -eq 1 && ${MININGCORE_ALLOW_BUILD_WARNINGS:-0} == 1 ]]; then
  echo "Build warning failure overridden for this user-initiated source build" >&2
  echo "Do not use this artifact for a release until every warning is resolved" >&2
  exit 0
fi

exit "$status"
