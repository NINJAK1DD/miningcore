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

# Inventory alone does not prove which runtime the application selects. The smoke
# below also reports and checks the runtime chosen with Miningcore's runtimeconfig.
dotnet --list-runtimes

expected_version="$(sed -nE \
  's/.*PackageReference Include="Portable.BouncyCastle" Version="([^"]+)".*/\1/p' \
  "$miningcore_project")"
[[ -n "$expected_version" ]] || fail \
  "could not determine Portable.BouncyCastle version from $miningcore_project"

expected_entry="\"Portable.BouncyCastle/$expected_version\""
grep -Fq "$expected_entry" "$dependency_manifest" || fail \
  "$dependency_manifest does not contain $expected_entry"

python3 "$script_dir/run-managed-dependency-smoke.py" "$publish_dir"
