#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
source "$repository_root/scripts/release/linux-release-targets.sh"

if ! checker_result=$(mktemp); then
  echo 'Unable to create private image-pin result file' >&2
  exit 70
fi

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
  unresolved_image_tags=()

  if ! mapfile -t unresolved_image_tags <"$checker_result"; then
    echo "Unable to read image-pin result file: $checker_result" >&2
    exit 70
  fi

  if (( ${#unresolved_image_tags[@]} == 0 )); then
    echo 'Image-pin checker returned transient status without a non-empty' \
      'unresolved-target result' >&2
    exit 70
  fi

  if (( ${#unresolved_image_tags[@]} > ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} )); then
    echo 'Image-pin checker returned more unresolved targets than are configured' >&2
    exit 70
  fi

  expected_index=0
  validated_image_tags=()
  canonical_targets=''

  for reported_tag in "${unresolved_image_tags[@]}"; do
    matched=false

    while (( expected_index < ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} )); do
      expected_image=$(miningcore_linux_release_target_image \
        "${MININGCORE_LINUX_RELEASE_TARGETS[$expected_index]}")
      expected_tag=${expected_image%@*}
      ((expected_index += 1))

      if [[ "$reported_tag" == "$expected_tag" ]]; then
        matched=true
        validated_image_tags+=("$expected_tag")
        break
      fi
    done

    if [[ "$matched" != true ]]; then
      # Do not repeat unvalidated handoff content in logs or workflow commands.
      echo 'Image-pin checker returned a non-canonical, unknown, duplicate, or' \
        'out-of-order unresolved target' >&2
      exit 70
    fi
  done

  for validated_tag in "${validated_image_tags[@]}"; do
    canonical_targets+="${canonical_targets:+, }$validated_tag"
  done

  # mapfile cannot represent NUL bytes. Compare the original bytes with a canonical
  # reserialization so binary contamination and a missing terminal newline also fail closed.
  if ! cmp -s "$checker_result" \
      <(printf '%s\n' "${validated_image_tags[@]}"); then
    echo 'Image-pin checker result does not exactly match the canonical line-oriented contract' \
      >&2
    exit 70
  fi

  warning="No drift decision for $canonical_targets; all configured targets were evaluated."
  printf '::warning title=Ubuntu image pin check unavailable::%s\n' "$warning"
  exit 0
fi

exit "$status"
