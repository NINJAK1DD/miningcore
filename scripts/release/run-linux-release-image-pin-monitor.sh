#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
checker_result=$(mktemp)

cleanup() {
  rm -f -- "$checker_result"
}
trap cleanup EXIT

set +e
# Keep stdout and stderr live; only the machine-readable transient result uses the private file.
MININGCORE_IMAGE_PIN_RESULT_FILE="$checker_result" bash "$checker"
status=$?
set -e

if [[ "$status" -eq 69 ]]; then
  mapfile -t summary_lines <"$checker_result"
  summary_prefix='Transient image checks unresolved: '

  if [[ "${#summary_lines[@]}" -ne 1 ||
      "${summary_lines[0]:-}" != "$summary_prefix"* ||
      "${summary_lines[0]:-}" = "$summary_prefix" ]]; then
    echo 'Image-pin checker returned transient status without exactly one valid' \
      'unresolved-target summary' >&2
    exit 70
  fi

  summary=${summary_lines[0]}
  unresolved_targets=${summary#"$summary_prefix"}
  warning="No drift decision for $unresolved_targets; all configured targets were evaluated."
  printf '::warning title=Ubuntu image pin check unavailable::%s\n' "$warning"
  exit 0
fi

exit "$status"
