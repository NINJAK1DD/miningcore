#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
source "$repository_root/scripts/release/linux-release-targets.sh"

if ! command -v docker >/dev/null 2>&1; then
  echo 'docker is required to resolve Docker Official Image manifest digests' >&2
  exit 70
fi

if ! docker buildx version >/dev/null 2>&1; then
  echo 'Docker Buildx is required to resolve Docker Official Image manifest digests' >&2
  exit 70
fi

if ! docker buildx imagetools inspect --help >/dev/null 2>&1; then
  echo 'Docker Buildx imagetools inspect is required to resolve image digests' >&2
  exit 70
fi

is_transient_registry_failure() {
  local failure=${1,,}
  local signature
  local -a transient_signatures=(
    'timeout'
    'timed out'
    'deadline exceeded'
    'connection refused'
    'connection reset'
    'temporary failure'
    'temporarily unavailable'
    'unexpected eof'
    'toomanyrequests'
    'too many requests'
    'rate limit'
    # Safe only while linux-release-targets.sh limits checks to Docker Official ubuntu: tags.
    # A private registry could use this diagnostic for a persistent configuration error.
    'no such host'
    'network is unreachable'
    'server misbehaving'
    '500 internal server error'
    'service unavailable'
    'bad gateway'
    'gateway timeout'
  )

  for signature in "${transient_signatures[@]}"; do
    if [[ "$failure" = *"$signature"* ]]; then
      return 0
    fi
  done

  return 1
}

saw_drift=false
saw_structural_failure=false
saw_transient_failure=false
transient_image_tags=()

# Inspect every target before choosing the strongest outcome: drift, structural failure, then
# transient failure. A recoverable outage must not suppress a conclusive result for another image.
for ubuntu_version in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
  pinned_image=$(miningcore_linux_release_target_image "$ubuntu_version")
  image_tag=${pinned_image%@*}
  expected_digest=${pinned_image#*@}

  if ! inspection=$(docker buildx imagetools inspect "$image_tag" 2>&1); then
    if is_transient_registry_failure "$inspection"; then
      echo "Transient registry failure while resolving $image_tag" >&2
      saw_transient_failure=true
      transient_image_tags+=("$image_tag")
    else
      echo "Unable to resolve $image_tag; the failure was not recognisably transient" >&2
      saw_structural_failure=true
    fi

    printf '%s\n' "$inspection" >&2
    continue
  fi

  current_digest=$(awk '$1 == "Digest:" { print $2; exit }' <<<"$inspection")

  if [[ ! "$current_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "Unable to resolve a manifest-list digest for $image_tag" >&2
    saw_structural_failure=true
    continue
  fi

  if [[ "$current_digest" != "$expected_digest" ]]; then
    echo "$image_tag now resolves to $current_digest" >&2
    echo "Reviewed release pin is $expected_digest" >&2
    echo 'Review upstream changes, run the complete release validation, then update the pin.' >&2
    saw_drift=true
    continue
  fi

  echo "$image_tag still matches reviewed pin $expected_digest"
done

if [[ "$saw_drift" = true ]]; then
  exit 1
fi

if [[ "$saw_structural_failure" = true ]]; then
  exit 70
fi

if [[ "$saw_transient_failure" = true ]]; then
  unresolved_targets=''

  for image_tag in "${transient_image_tags[@]}"; do
    if [[ -n "$unresolved_targets" ]]; then
      unresolved_targets+=', '
    fi

    unresolved_targets+=$image_tag
  done

  summary="Transient image checks unresolved: $unresolved_targets"

  # The workflow wrapper supplies a private result file so human diagnostics can remain live.
  if [[ -n ${MININGCORE_IMAGE_PIN_RESULT_FILE:-} ]]; then
    if ! printf '%s\n' "$summary" >"$MININGCORE_IMAGE_PIN_RESULT_FILE"; then
      echo "Unable to write image-pin result file: $MININGCORE_IMAGE_PIN_RESULT_FILE" >&2
      exit 70
    fi
  else
    printf '%s\n' "$summary" >&2
  fi

  exit 69
fi
