#!/usr/bin/env sh
set -eu
# Run as the image's normal user, with its own runtime and the exact /app artifacts.
probe_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
runtime=$(mktemp -d)
trap 'rm -rf -- "$runtime"' EXIT
cp -a /app/. "$runtime/"
cp "$probe_dir/Miningcore.ManagedDependencySmoke.dll" "$runtime/"
read -r version mode < "$probe_dir/pkcs-binding.txt"
dotnet exec --depsfile "$runtime/Miningcore.deps.json" \
  --runtimeconfig "$runtime/Miningcore.runtimeconfig.json" \
  "$runtime/Miningcore.ManagedDependencySmoke.dll" "$version" "$mode"
