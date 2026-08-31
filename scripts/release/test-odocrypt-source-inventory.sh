#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
manifest="$repository_root/src/Native/libodocrypt/upstream.sha256"
managed_project="$repository_root/src/Miningcore/Miningcore.csproj"
makefile="$repository_root/src/Native/libodocrypt/Makefile"
native_project="$repository_root/src/Native/libodocrypt/libodocrypt.vcxproj"

expected_paths=$(cat <<'EOF'
libmultihash/KeccakP-800-SnP.h
libmultihash/KeccakP-800-reference.c
libmultihash/brg_endian.h
libmultihash/odocrypt.cpp
libmultihash/odocrypt.h
EOF
)
actual_paths=$(awk '{ print $2 }' "$manifest" | LC_ALL=C sort)

if [[ "$actual_paths" != "$expected_paths" ]]; then
  echo "Odocrypt pinned-source manifest does not contain the exact reviewed native input set" >&2
  diff -u <(printf '%s\n' "$expected_paths") <(printf '%s\n' "$actual_paths") >&2 || true
  exit 1
fi

for source_path in $expected_paths; do
  if [[ ! -f "$repository_root/src/Native/$source_path" ]]; then
    echo "Odocrypt reviewed native input is missing: $source_path" >&2
    exit 1
  fi
done

grep -Fq "<OdoCryptWindowsBuildInputs Include=\"\$(ProjectDir)..\Native\libmultihash\brg_endian.h\" />" \
  "$managed_project" || {
    echo "Windows outer build does not track brg_endian.h" >&2
    exit 1
  }

grep -Fq 'KeccakP-800-reference.o: ../libmultihash/KeccakP-800-reference.c ../libmultihash/KeccakP-800-SnP.h ../libmultihash/brg_endian.h' \
  "$makefile" || {
    echo "Linux Odocrypt dependency graph does not track brg_endian.h" >&2
    exit 1
  }

grep -Fq '<ClInclude Include="..\libmultihash\brg_endian.h" />' "$native_project" || {
  echo "Windows native project does not declare brg_endian.h" >&2
  exit 1
}

echo "Odocrypt reviewed native-source inventory is complete"
