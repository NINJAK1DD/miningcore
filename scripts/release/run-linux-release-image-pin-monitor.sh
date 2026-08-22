#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
checker_error=$(mktemp)

cleanup() {
  rm -f -- "$checker_error"
}
trap cleanup EXIT

set +e
bash "$checker" 2>"$checker_error"
status=$?
set -e

cat "$checker_error" >&2

if [[ "$status" -eq 69 ]]; then
  summary=$(tail -n 1 "$checker_error")
  summary_prefix='Transient image checks unresolved: '

  if [[ "$summary" != "$summary_prefix"* || "$summary" = "$summary_prefix" ]]; then
    echo 'Image-pin checker returned transient status without an unresolved-target summary' >&2
    exit 70
  fi

  unresolved_targets=${summary#"$summary_prefix"}
  warning="No drift decision for $unresolved_targets; all configured targets were evaluated."
  printf '::warning title=Ubuntu image pin check unavailable::%s\n' "$warning"
  exit 0
fi

exit "$status"
