#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
source "$repository_root/scripts/release/linux-release-targets.sh"

if ! checker_result=$(mktemp); then
  echo 'Unable to create private image-pin result file' >&2
  exit 70
fi

# The EXIT trap is the only caller; ShellCheck cannot infer that callback edge. ShellCheck 0.9
# reports SC2317 while 0.11 reports the same indirect-callback condition as SC2329.
# shellcheck disable=SC2317,SC2329
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
  expected_image_tags=()

  for target in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
    expected_image=$(miningcore_linux_release_target_image "$target")
    expected_image_tags+=("${expected_image%@*}")
  done

  # Read no more than one line beyond the complete configured set. The extra line proves an
  # overlong result without loading an arbitrarily long multi-line file into memory.
  if ! mapfile -t -n "$(( ${#expected_image_tags[@]} + 1 ))" \
      unresolved_image_tags <"$checker_result"; then
    echo "Unable to read image-pin result file: $checker_result" >&2
    exit 70
  fi

  if (( ${#unresolved_image_tags[@]} == 0 )); then
    echo 'Image-pin checker returned transient status without a non-empty' \
      'unresolved-target result' >&2
    exit 70
  fi

  expected_index=0
  validated_image_tags=()
  canonical_targets=''
  reported_line=0

  for reported_tag in "${unresolved_image_tags[@]}"; do
    matched=false
    ((reported_line += 1))

    while (( expected_index < ${#expected_image_tags[@]} )); do
      expected_tag=${expected_image_tags[$expected_index]}
      ((expected_index += 1))

      if [[ "$reported_tag" == "$expected_tag" ]]; then
        matched=true
        validated_image_tags+=("$expected_tag")
        canonical_targets+="${canonical_targets:+, }$expected_tag"
        break
      fi
    done

    if [[ "$matched" != true ]]; then
      # Do not repeat unvalidated handoff content in logs or workflow commands.
      echo 'Image-pin checker returned a non-canonical, unknown, duplicate, or' \
        "out-of-order unresolved target at line $reported_line" >&2
      exit 70
    fi
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
