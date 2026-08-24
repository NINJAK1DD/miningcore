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
bash "$script_dir/test-linux-native-inventory.sh" "$publish_dir"

mapfile -t actual_libraries < <(
  find "$publish_dir" -maxdepth 1 -type f -name '*.so' -printf '%f\n' | sort
)

for library_name in "${actual_libraries[@]}"; do
  library="$publish_dir/$library_name"
  description=$(file -b "$library")

  if [[ "$description" != *"ELF 64-bit LSB shared object, x86-64"* ]]; then
    echo "$library_name has an unexpected format: $description" >&2
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
