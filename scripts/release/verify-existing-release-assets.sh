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

expected_count=${#MININGCORE_LINUX_RELEASE_TARGETS[@]}

if [[ ${#candidate_archives[@]} -ne $expected_count ]]; then
  printf 'Candidate release requires %d Ubuntu archives, found %d\n' \
    "$expected_count" "${#candidate_archives[@]}" >&2
  exit 1
fi

if [[ ${#existing_archives[@]} -ne $expected_count ]]; then
  printf 'Existing release contains %d Ubuntu archive(s); the current format requires %d.\n' \
    "${#existing_archives[@]}" "$expected_count" >&2
  echo "The release may predate the dual-archive format; stop and review it manually." >&2
  echo "Do not overwrite or retry publication for an existing version tag." >&2
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
