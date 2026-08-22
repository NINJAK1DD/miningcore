#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
source "$repository_root/scripts/release/linux-release-targets.sh"
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
  if ! mapfile -t summary_lines <"$checker_result"; then
    echo "Unable to read image-pin result file: $checker_result" >&2
    exit 70
  fi

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

  IFS=',' read -r -a unresolved_image_tags <<<"$unresolved_targets"
  expected_index=0

  for reported_index in "${!unresolved_image_tags[@]}"; do
    reported_tag=${unresolved_image_tags[$reported_index]}

    if (( reported_index > 0 )); then
      if [[ "$reported_tag" != ' '* ]]; then
        echo 'Image-pin checker returned a non-canonical unresolved-target list' >&2
        exit 70
      fi

      reported_tag=${reported_tag# }
    fi

    matched=false

    while (( expected_index < ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} )); do
      expected_image=$(miningcore_linux_release_target_image \
        "${MININGCORE_LINUX_RELEASE_TARGETS[$expected_index]}")
      expected_tag=${expected_image%@*}
      ((expected_index += 1))

      if [[ "$reported_tag" == "$expected_tag" ]]; then
        matched=true
        break
      fi
    done

    if [[ "$matched" != true ]]; then
      echo "Image-pin checker returned an unknown, duplicate, or out-of-order target: " \
        "$reported_tag" >&2
      exit 70
    fi
  done

  warning="No drift decision for $unresolved_targets; all configured targets were evaluated."
  printf '::warning title=Ubuntu image pin check unavailable::%s\n' "$warning"
  exit 0
fi

exit "$status"
