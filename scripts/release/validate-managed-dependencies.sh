#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <publish-directory>" >&2
  exit 2
fi

publish_dir="$(realpath "$1")"

test -f "$publish_dir/BouncyCastle.Cryptography.dll"
test -f "$publish_dir/BouncyCastle.Crypto.dll"
grep -Fq '"Portable.BouncyCastle/1.8.2"' "$publish_dir/Miningcore.deps.json"

smoke_output="$(mktemp -d)"
smoke_assembly="$publish_dir/Miningcore.ManagedDependencySmoke.dll"
trap 'rm -f -- "$smoke_assembly"' EXIT

dotnet build scripts/release/managed-dependency-smoke/Miningcore.ManagedDependencySmoke.csproj \
  --configuration Release \
  --output "$smoke_output" \
  -p:MiningcorePublishDir="$publish_dir"
cp "$smoke_output/Miningcore.ManagedDependencySmoke.dll" "$smoke_assembly"
dotnet exec \
  --depsfile "$publish_dir/Miningcore.deps.json" \
  --runtimeconfig "$publish_dir/Miningcore.runtimeconfig.json" \
  "$smoke_assembly"
