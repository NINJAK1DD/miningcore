#!/usr/bin/env bash

set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source "$script_directory/source-build-identity.sh"

test_repository="$(mktemp -d)"
trap 'rm -rf "$test_repository"' EXIT

git -C "$test_repository" init --quiet
git -C "$test_repository" config user.name 'Miningcore Tests'
git -C "$test_repository" config user.email 'tests@miningcore.invalid'
printf 'source\n' > "$test_repository/source.txt"
git -C "$test_repository" add source.txt
git -C "$test_repository" commit --quiet -m 'test source'

identity_arguments=()
miningcore_resolve_source_build_identity "$test_repository" identity_arguments
if (( ${#identity_arguments[@]} != 0 )); then
  echo 'An untagged source build unexpectedly received a release identity' >&2
  exit 1
fi

git -C "$test_repository" tag -a v1.2.3-rc.4 -m 'test release'
source_commit="$(git -C "$test_repository" rev-parse HEAD)"
miningcore_resolve_source_build_identity "$test_repository" identity_arguments

expected_arguments=(
  '-p:MiningcoreReleaseVersion=1.2.3-rc.4'
  "-p:MiningcoreSourceCommit=$source_commit"
)

if [[ "${identity_arguments[*]}" != "${expected_arguments[*]}" ]]; then
  printf 'Unexpected release identity arguments: %s\n' "${identity_arguments[*]}" >&2
  exit 1
fi

printf 'modified\n' >> "$test_repository/source.txt"
if miningcore_resolve_source_build_identity "$test_repository" identity_arguments; then
  echo 'A dirty tagged source build unexpectedly received a release identity' >&2
  exit 1
fi
git -C "$test_repository" restore source.txt

git -C "$test_repository" tag -a v1.2.3-rc.5 -m 'ambiguous release'
if miningcore_resolve_source_build_identity "$test_repository" identity_arguments; then
  echo 'A multiply-tagged source build unexpectedly received a release identity' >&2
  exit 1
fi

echo 'Source-build identity tests passed'
