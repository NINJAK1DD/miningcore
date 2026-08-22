#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"

set +e
bash "$checker"
status=$?
set -e

if [[ "$status" -eq 69 ]]; then
  warning='The registry or digest resolver was unavailable; no image drift decision was made.'
  printf '::warning title=Ubuntu image pin check unavailable::%s\n' "$warning"
  exit 0
fi

exit "$status"
