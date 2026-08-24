#!/bin/bash

set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 PUBLISH_DIRECTORY UBUNTU_VERSION" >&2
  exit 64
fi

publish_dir=$1
ubuntu_version=$2

case "$ubuntu_version" in
  24.04|26.04)
    ;;
  *)
    echo "Unsupported Ubuntu source-build target: $ubuntu_version" >&2
    exit 64
    ;;
esac

smoke_id="ubuntu${ubuntu_version//./}-smoke"
publish_dir=$(realpath "$publish_dir")
app="$publish_dir/Miningcore"

if [ ! -x "$app" ]; then
  echo "Published Miningcore executable is missing: $app" >&2
  exit 1
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd "$script_dir/../.." && pwd)
bash "$script_dir/test-linux-native-inventory.sh" "$publish_dir"

mapfile -t actual_libraries < <(
  find "$publish_dir" -maxdepth 1 -type f -name '*.so' -printf '%f\n' | sort
)

missing_dependencies=0

for library_name in "${actual_libraries[@]}"; do
  library="$publish_dir/$library_name"
  description=$(file -b "$library")

  if [[ "$description" != *"ELF 64-bit LSB shared object, x86-64"* ]]; then
    echo "$library_name has an unexpected format: $description" >&2
    exit 1
  fi

  if ! dependencies=$(ldd "$library" 2>&1); then
    echo "$library_name could not be inspected with ldd:" >&2
    printf '%s\n' "$dependencies" >&2
    exit 1
  fi

  if grep -Fq 'not found' <<<"$dependencies"; then
    echo "$library_name has unresolved native dependencies:" >&2
    printf '%s\n' "$dependencies" >&2
    missing_dependencies=1
  fi
done

if [ "$missing_dependencies" -ne 0 ]; then
  exit 1
fi

# Do not use `ldd -r` as a blanket gate here. Some hashing plugins intentionally retain
# lazy/optional symbols that are supplied only by the algorithm path that consumes them. The
# libmultihash link uses --no-undefined because all of its dependencies are known at build time.
# Companion native tests load other changed libraries and call their reviewed algorithm paths.
if ! multihash_relocations=$(ldd -r "$publish_dir/libmultihash.so" 2>&1); then
  echo "libmultihash.so failed dynamic relocation validation:" >&2
  printf '%s\n' "$multihash_relocations" >&2
  exit 1
fi

if grep -Fq 'undefined symbol:' <<<"$multihash_relocations"; then
  echo "libmultihash.so contains an unresolved dynamic symbol:" >&2
  printf '%s\n' "$multihash_relocations" >&2
  exit 1
fi

multihash_source="$repository_root/src/Miningcore/Native/Multihash.cs"

if [[ ! -f "$multihash_source" || ! -r "$multihash_source" ]]; then
  echo "Unable to read the managed libmultihash import contract" >&2
  exit 1
fi

if ! multihash_imports=$(sed -nE \
    's/.*EntryPoint[[:space:]]*=[[:space:]]*"([^"]+)".*/\1/p' \
    "$multihash_source" | sort -u); then
  echo "Unable to inspect managed libmultihash entry points" >&2
  exit 1
fi

if [[ -z "$multihash_imports" ]]; then
  echo "Managed libmultihash entry-point contract is empty" >&2
  exit 1
fi

if ! multihash_exports=$(nm -D --defined-only "$publish_dir/libmultihash.so" | \
    awk '{ print $3 }' | sort -u); then
  echo "Unable to inspect exported symbols in libmultihash.so" >&2
  exit 1
fi

missing_multihash_import=0

while IFS= read -r symbol; do
  if ! grep -Fxq "$symbol" <<<"$multihash_exports"; then
    echo "libmultihash.so does not export managed entry point: $symbol" >&2
    missing_multihash_import=1
  fi
done <<<"$multihash_imports"

if [[ "$missing_multihash_import" -ne 0 ]]; then
  exit 1
fi

if ! zanonote_symbols=$(nm -D --defined-only "$publish_dir/libzanonote.so" | \
    awk '{ print $3 }'); then
  echo "Unable to inspect exported symbols in libzanonote.so" >&2
  exit 1
fi

for symbol in \
    convert_blob_export \
    convert_block_export \
    get_blob_id_export \
    get_block_id_export; do
  if ! grep -Fxq "$symbol" <<<"$zanonote_symbols"; then
    echo "libzanonote.so does not export required entry point: $symbol" >&2
    exit 1
  fi
done

run_miningcore() {
  LD_LIBRARY_PATH="$publish_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    "$app" "$@"
}

run_miningcore --version
run_miningcore --help > /dev/null

work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT

generated_schema="$work_dir/config.schema.json"
run_miningcore --generate-config-schema "$generated_schema"

python3 - "$generated_schema" <<'PY'
import json
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
with path.open(encoding="utf-8-sig") as stream:
    json.load(stream)
PY

if ! cmp -s "$generated_schema" "$publish_dir/config.schema.json"; then
  echo "Generated configuration schema differs from the published schema" >&2
  sha256sum "$generated_schema" "$publish_dir/config.schema.json" >&2
  exit 1
fi

missing_pfx="$work_dir/missing-runtime-smoke.pfx"
smoke_config="$work_dir/runtime-smoke.json"

cat > "$smoke_config" <<JSON
{
  "clusterName": "$smoke_id-runtime",
  "api": {
    "enabled": true,
    "listenAddress": "127.0.0.1",
    "port": 41990,
    "adminPort": 41991,
    "metricsPort": 41992,
    "tls": {
      "enabled": true,
      "tlsPfxFile": "$missing_pfx",
      "tlsPfxPassword": "runtime-smoke-only"
    }
  },
  "paymentProcessing": {
    "enabled": false
  },
  "pools": [
    {
      "id": "$smoke_id",
      "enabled": true,
      "coin": "litecoin",
      "address": "RUNTIME_SMOKE_TEST_ONLY",
      "enableInternalStratum": false,
      "ports": {},
      "daemons": [
        {
          "host": "127.0.0.1",
          "port": 1
        }
      ],
      "paymentProcessing": {
        "enabled": false,
        "minimumPayment": 0.01,
        "payoutScheme": "SOLO"
      }
    }
  ],
  "notifications": {
    "enabled": false,
    "email": {
      "host": "127.0.0.1",
      "port": 1,
      "fromAddress": "smoke-test@example.invalid",
      "fromName": "Ubuntu $ubuntu_version smoke test"
    },
    "admin": {
      "enabled": false
    },
    "pushover": {
      "enabled": false
    }
  }
}
JSON

set +e
smoke_output=$(run_miningcore -c "$smoke_config" 2>&1)
smoke_status=$?
set -e

printf '%s\n' "$smoke_output"

if [ "$smoke_status" -ne 1 ]; then
  echo "Runtime preflight returned $smoke_status instead of the expected safety-boundary status 1" >&2
  exit 1
fi

if ! grep -Fq "Certificate file $missing_pfx does not exist!" <<<"$smoke_output"; then
  echo "Runtime preflight did not report the expected missing-certificate boundary" >&2
  exit 1
fi

if ! grep -Fq 'Cluster cannot start. Good Bye!' <<<"$smoke_output"; then
  echo "Runtime preflight did not reach the expected controlled shutdown boundary" >&2
  exit 1
fi

echo "Ubuntu $ubuntu_version source-build artifact validation passed"
