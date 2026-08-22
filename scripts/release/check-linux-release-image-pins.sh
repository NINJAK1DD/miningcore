#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
source "$repository_root/scripts/release/linux-release-targets.sh"

if ! command -v docker >/dev/null 2>&1; then
  echo 'docker is required to resolve Docker Official Image manifest digests' >&2
  exit 70
fi

for ubuntu_version in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
  pinned_image=$(miningcore_linux_release_target_image "$ubuntu_version")
  image_tag=${pinned_image%@*}
  expected_digest=${pinned_image#*@}

  if ! inspection=$(docker buildx imagetools inspect "$image_tag" 2>&1); then
    echo "Unable to reach the registry while resolving $image_tag" >&2
    printf '%s\n' "$inspection" >&2
    exit 69
  fi

  current_digest=$(awk '$1 == "Digest:" { print $2; exit }' <<<"$inspection")

  if [[ ! "$current_digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "Unable to resolve a manifest-list digest for $image_tag" >&2
    exit 70
  fi

  if [[ "$current_digest" != "$expected_digest" ]]; then
    echo "$image_tag now resolves to $current_digest" >&2
    echo "Reviewed release pin is $expected_digest" >&2
    echo 'Review upstream changes, run the complete release validation, then update the pin.' >&2
    exit 1
  fi

  echo "$image_tag still matches reviewed pin $expected_digest"
done
