#!/bin/bash

set -euo pipefail

publish_dir=${1:?"usage: $0 PUBLISH_DIRECTORY"}
publish_dir=$(realpath "$publish_dir")
app="$publish_dir/Miningcore"

if [ ! -x "$app" ]; then
  echo "Published Miningcore executable is missing: $app" >&2
  exit 1
fi

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
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

# These two compatibility changes remove obsolete Boost.System linkage. Resolve every
# relocation now so a missing symbol cannot remain hidden until the first native call.
for library_name in libcryptonote.so libzanonote.so; do
  library="$publish_dir/$library_name"

  if ! relocations=$(ldd -r "$library" 2>&1); then
    echo "$library_name failed relocation validation:" >&2
    printf '%s\n' "$relocations" >&2
    exit 1
  fi

  if grep -Eq 'not found|undefined symbol' <<<"$relocations"; then
    echo "$library_name contains an unresolved relocation:" >&2
    printf '%s\n' "$relocations" >&2
    exit 1
  fi
done

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
  "clusterName": "ubuntu2604-runtime-smoke",
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
      "id": "ubuntu2604-smoke",
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
      "fromName": "Ubuntu 26.04 smoke test"
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

grep -F "Certificate file $missing_pfx does not exist!" <<<"$smoke_output" > /dev/null
grep -F 'Cluster cannot start. Good Bye!' <<<"$smoke_output" > /dev/null

echo "Ubuntu 26.04 source-build artifact validation passed"
