#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
manifest="$repository_root/src/Native/libodocrypt/upstream.sha256"
managed_project="$repository_root/src/Miningcore/Miningcore.csproj"
makefile="$repository_root/src/Native/libodocrypt/Makefile"
native_project="$repository_root/src/Native/libodocrypt/libodocrypt.vcxproj"
native_root="$repository_root/src/Native"
odocrypt_root="$native_root/libodocrypt"
c_compiler=${CC:-cc}
cxx_compiler=${CXX:-c++}

for compiler in "$c_compiler" "$cxx_compiler"; do
  if ! command -v "$compiler" >/dev/null 2>&1; then
    echo "A C and C++ compiler are required to derive the Odocrypt source closure" >&2
    exit 70
  fi
done

manifest_paths=$(awk '{ print $2 }' "$manifest" | LC_ALL=C sort)
compiler_paths=$(
  cd "$odocrypt_root"
  {
    "$cxx_compiler" -MM exports.cpp
    "$cxx_compiler" -MM ../libmultihash/odocrypt.cpp
    "$c_compiler" -MM ../libmultihash/KeccakP-800-reference.c
  } |
    awk '{ for(field = 1; field <= NF; field++) if($field ~ /^\.\.\/libmultihash\//) print substr($field, 4) }' |
    LC_ALL=C sort -u
)

if [[ -z "$compiler_paths" || "$manifest_paths" != "$compiler_paths" ]]; then
  echo "Odocrypt pinned-source manifest does not match the compiler-derived native input closure" >&2
  diff -u <(printf '%s\n' "$manifest_paths") <(printf '%s\n' "$compiler_paths") >&2 || true
  exit 1
fi

require_build_input()
{
  local file=$1
  local input=$2
  local description=$3

  if ! grep -Fq -- "$input" "$file"; then
    echo "$description does not track $input" >&2
    exit 1
  fi
}

while IFS= read -r source_path; do
  if [[ ! -f "$native_root/$source_path" ]]; then
    echo "Odocrypt reviewed native input is missing: $source_path" >&2
    exit 1
  fi

  windows_path=${source_path//\//\\}
  require_build_input "$makefile" "../$source_path" \
    "Linux Odocrypt dependency graph"
  require_build_input "$native_project" "..\\$windows_path" \
    "Windows native project"
  require_build_input "$managed_project" \
    "\$(ProjectDir)..\\Native\\$windows_path" "Windows outer build"
done <<< "$manifest_paths"

require_build_input "$makefile" 'exports.cpp' "Linux Odocrypt dependency graph"
require_build_input "$native_project" '<ClCompile Include="exports.cpp" />' \
  "Windows native project"
require_build_input "$managed_project" \
  "<OdoCryptWindowsBuildInputs Include=\"\$(ProjectDir)..\Native\libodocrypt\exports.cpp\" />" \
  "Windows outer build"

echo "Odocrypt reviewed native-source inventory is complete"
