#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <release-asset-directory> <expected-version> <expected-source-commit>" >&2
  exit 64
fi

asset_dir=$(realpath "$1")
expected_version=$2
expected_source_commit=$3
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
source "$repository_root/scripts/release/linux-release-targets.sh"

version_pattern='v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
version_pattern+='(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?'
expected_version_pattern="^${version_pattern}$"

if [[ ! "$expected_version" =~ $expected_version_pattern ]]; then
  echo "Expected release version is invalid: $expected_version" >&2
  exit 64
fi

if [[ ! "$expected_source_commit" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ ]]; then
  echo "Expected source commit is invalid: $expected_source_commit" >&2
  exit 64
fi

mapfile -t archives < <(
  find "$asset_dir" -maxdepth 1 -type f \
    -name 'miningcore-*-linux-x64-ubuntu-*.tar.gz' \
    -printf '%f\n' | sort
)

if [[ ${#archives[@]} -ne ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} ]]; then
  printf 'Expected exactly %d Ubuntu archives, found %d:\n' \
    "${#MININGCORE_LINUX_RELEASE_TARGETS[@]}" "${#archives[@]}" >&2
  printf '  %s\n' "${archives[@]}" >&2
  exit 1
fi

declare -A archive_by_target=()
filename_pattern="^miningcore-(${version_pattern})"
filename_pattern+='-linux-x64-ubuntu-([0-9]+\.[0-9]+)\.tar\.gz$'

for archive in "${archives[@]}"; do
  if [[ ! "$archive" =~ $filename_pattern ]]; then
    echo "Ubuntu archive has an invalid filename: $archive" >&2
    exit 1
  fi

  recorded_filename_version=${BASH_REMATCH[1]}
  ubuntu_version=${BASH_REMATCH[7]}

  if ! miningcore_linux_release_target_supported "$ubuntu_version"; then
    echo "Unsupported Ubuntu archive target in filename: $ubuntu_version ($archive)" >&2
    exit 1
  fi

  if [[ "$recorded_filename_version" != "$expected_version" ]]; then
    echo "$archive records release $recorded_filename_version; expected $expected_version" >&2
    exit 1
  fi

  if [[ -n "${archive_by_target[$ubuntu_version]:-}" ]]; then
    echo "Expected exactly one Ubuntu $ubuntu_version archive" >&2
    exit 1
  fi

  archive_by_target[$ubuntu_version]=$archive
done

for ubuntu_version in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
  expected_archive="miningcore-${expected_version}-linux-x64-ubuntu-${ubuntu_version}.tar.gz"
  archive=${archive_by_target[$ubuntu_version]:-}

  if [[ "$archive" != "$expected_archive" ]]; then
    echo "Expected exactly one Ubuntu $ubuntu_version archive named $expected_archive" >&2
    exit 1
  fi

  archive_path="$asset_dir/$archive"
  package_root=${archive%.tar.gz}
  build_info_path="$package_root/BUILD-INFO"

  if ! tar -tzf "$archive_path" >/dev/null; then
    echo "$archive is not a valid gzip-compressed tar archive" >&2
    exit 1
  fi

  # Do not use grep -q here: with pipefail it may close the pipe early and
  # misclassify tar's resulting SIGPIPE as a missing metadata entry.
  if ! tar -tzf "$archive_path" | grep -Fx "$build_info_path" >/dev/null; then
    echo "$archive does not contain $build_info_path" >&2
    exit 1
  fi

  build_info=$(tar -xOf "$archive_path" "$build_info_path")
  recorded_version=$(sed -n 's/^Version: //p' <<<"$build_info")
  recorded_commit=$(sed -n 's/^Source commit: //p' <<<"$build_info")
  recorded_target=$(sed -n 's/^Target: //p' <<<"$build_info")

  if [[ "$recorded_version" != "$expected_version" ]]; then
    echo "$archive records an unexpected version: $recorded_version" >&2
    exit 1
  fi

  if [[ "$recorded_target" != "Ubuntu $ubuntu_version x64" ]]; then
    echo "$archive records an unexpected target: $recorded_target" >&2
    exit 1
  fi

  if [[ ! "$recorded_commit" =~ ^[0-9a-f]{40}([0-9a-f]{24})?$ ]]; then
    echo "$archive records an invalid source commit: $recorded_commit" >&2
    exit 1
  fi

  if [[ "$recorded_commit" != "$expected_source_commit" ]]; then
    echo "$archive records source commit $recorded_commit; expected $expected_source_commit" >&2
    exit 1
  fi
done

(
  cd "$asset_dir"
  sha256sum "${archives[@]}" > SHA256SUMS
  sha256sum --check --strict SHA256SUMS
)

echo "Validated release assets for Ubuntu targets: $(miningcore_linux_release_target_list)"
