#!/usr/bin/env bash

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "Usage: $0 <release-asset-directory>" >&2
  exit 64
fi

asset_dir=$(realpath "$1")

mapfile -t archives < <(
  find "$asset_dir" -maxdepth 1 -type f \
    -name 'miningcore-*-linux-x64-ubuntu-*.tar.gz' \
    -printf '%f\n' | sort
)

if [ "${#archives[@]}" -ne 2 ]; then
  printf 'Expected exactly two Ubuntu archives, found %d:\n' "${#archives[@]}" >&2
  printf '  %s\n' "${archives[@]}" >&2
  exit 1
fi

mapfile -t release_prefixes < <(
  printf '%s\n' "${archives[@]}" |
    sed -E 's/-linux-x64-ubuntu-(22\.04|26\.04)\.tar\.gz$//' |
    sort -u
)

if [ "${#release_prefixes[@]}" -ne 1 ] ||
    [ "${release_prefixes[0]}" = "${archives[0]}" ]; then
  echo "Ubuntu archives do not describe one release version" >&2
  printf '  %s\n' "${archives[@]}" >&2
  exit 1
fi

expected_version=${release_prefixes[0]#miningcore-}
source_commit=

for ubuntu_version in 22.04 26.04; do
  expected_archive="${release_prefixes[0]}-linux-x64-ubuntu-${ubuntu_version}.tar.gz"

  if [ ! -f "$asset_dir/$expected_archive" ]; then
    echo "Expected exactly one Ubuntu $ubuntu_version archive" >&2
    exit 1
  fi

  package_root=${expected_archive%.tar.gz}
  build_info=$(tar -xOf "$asset_dir/$expected_archive" "$package_root/BUILD-INFO")
  recorded_version=$(sed -n 's/^Version: //p' <<<"$build_info")
  recorded_commit=$(sed -n 's/^Source commit: //p' <<<"$build_info")
  recorded_target=$(sed -n 's/^Target: //p' <<<"$build_info")

  if [ "$recorded_version" != "$expected_version" ]; then
    echo "$expected_archive records an unexpected version: $recorded_version" >&2
    exit 1
  fi

  if [ "$recorded_target" != "Ubuntu $ubuntu_version x64" ]; then
    echo "$expected_archive records an unexpected target: $recorded_target" >&2
    exit 1
  fi

  if [[ ! "$recorded_commit" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ ]]; then
    echo "$expected_archive records an invalid source commit: $recorded_commit" >&2
    exit 1
  fi

  if [ -n "$source_commit" ] && [ "$recorded_commit" != "$source_commit" ]; then
    echo "Ubuntu archives were built from different source commits" >&2
    exit 1
  fi

  source_commit=$recorded_commit
  tar -tzf "$asset_dir/$expected_archive" >/dev/null
done

(
  cd "$asset_dir"
  sha256sum "${archives[@]}" > SHA256SUMS
  sha256sum --check --strict SHA256SUMS
)

echo "Validated Ubuntu 26.04 primary and Ubuntu 22.04 compatibility release assets"
