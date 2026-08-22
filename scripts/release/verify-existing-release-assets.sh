#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 <candidate-asset-directory> <existing-release-directory>" >&2
  exit 64
fi

candidate_dir=$(realpath "$1")
existing_dir=$(realpath "$2")
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
source "$repository_root/scripts/release/linux-release-targets.sh"

for directory in "$candidate_dir" "$existing_dir"; do
  if [[ ! -f "$directory/SHA256SUMS" ]]; then
    echo "$directory does not contain SHA256SUMS" >&2
    exit 1
  fi
done

mapfile -t candidate_archives < <(
  find "$candidate_dir" -maxdepth 1 -type f \
    -name 'miningcore-*-linux-x64-ubuntu-*.tar.gz' \
    -printf '%f\n' | sort
)
mapfile -t existing_archives < <(
  find "$existing_dir" -maxdepth 1 -type f \
    -name 'miningcore-*-linux-x64-ubuntu-*.tar.gz' \
    -printf '%f\n' | sort
)

if [[ ${#candidate_archives[@]} -ne ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} ]] ||
    [[ ${#existing_archives[@]} -ne ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} ]]; then
  echo "Existing-release comparison requires the complete Ubuntu archive set" >&2
  exit 1
fi

if [[ "${candidate_archives[*]}" != "${existing_archives[*]}" ]]; then
  echo "Existing release has a different Ubuntu archive set" >&2
  printf '  candidate: %s\n' "${candidate_archives[*]}" >&2
  printf '  existing:  %s\n' "${existing_archives[*]}" >&2
  exit 1
fi

cmp "$candidate_dir/SHA256SUMS" "$existing_dir/SHA256SUMS"

for archive in "${candidate_archives[@]}"; do
  cmp "$candidate_dir/$archive" "$existing_dir/$archive"
done

echo "Existing release assets are byte-identical"
