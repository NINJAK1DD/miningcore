#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <publish-directory>" >&2
  exit 2
fi

fail()
{
  echo "Managed dependency validation failed: $*" >&2
  exit 1
}

[[ -d "$1" ]] || fail "publish directory does not exist: $1"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
publish_dir="$(realpath "$1")"
miningcore_project="$repo_root/src/Miningcore/Miningcore.csproj"
dependency_manifest="$publish_dir/Miningcore.deps.json"

require_file()
{
  [[ -f "$1" ]] || fail "required file is missing: $1"
}

require_file "$miningcore_project"
require_file "$publish_dir/Miningcore.dll"
require_file "$publish_dir/Miningcore.runtimeconfig.json"
require_file "$publish_dir/NBitcoin.Zcash.dll"
require_file "$publish_dir/BouncyCastle.Cryptography.dll"
require_file "$publish_dir/BouncyCastle.Crypto.dll"
require_file "$dependency_manifest"

expected_version="$(sed -nE \
  's/.*PackageReference Include="Portable.BouncyCastle" Version="([^"]+)".*/\1/p' \
  "$miningcore_project")"
[[ -n "$expected_version" ]] || fail \
  "could not determine Portable.BouncyCastle version from $miningcore_project"

expected_entry="\"Portable.BouncyCastle/$expected_version\""
grep -Fq "$expected_entry" "$dependency_manifest" || fail \
  "$dependency_manifest does not contain $expected_entry"

smoke_output="$(mktemp -d)"
smoke_build="$smoke_output/build"
smoke_runtime="$smoke_output/runtime"
trap 'rm -rf -- "$smoke_output"' EXIT

mkdir -p "$smoke_runtime"
cp -a "$publish_dir/." "$smoke_runtime/"

dotnet build "$script_dir/managed-dependency-smoke/Miningcore.ManagedDependencySmoke.csproj" \
  --configuration Release \
  --output "$smoke_build" \
  -p:MiningcorePublishDir="$publish_dir"
cp "$smoke_build/Miningcore.ManagedDependencySmoke.dll" "$smoke_runtime/"
dotnet exec \
  --depsfile "$smoke_runtime/Miningcore.deps.json" \
  --runtimeconfig "$smoke_runtime/Miningcore.runtimeconfig.json" \
  "$smoke_runtime/Miningcore.ManagedDependencySmoke.dll"
