#!/usr/bin/env bash

set -euo pipefail

publish_dir=${1:?usage: test-linux-native-inventory.sh PUBLISH_DIRECTORY}
publish_dir=$(realpath "$publish_dir")
script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd "$script_dir/../.." && pwd)
inventory="$script_dir/linux-native-libraries.txt"

if [[ ! -f "$inventory" ]]; then
  echo "Linux native-library inventory is missing: $inventory" >&2
  exit 1
fi

mapfile -t expected_libraries < <(
  sed -e 's/\r$//' -e '/^[[:space:]]*$/d' "$inventory" | sort
)
mapfile -t actual_libraries < <(
  find "$publish_dir" -maxdepth 1 -type f -name '*.so' -printf '%f\n' | sort
)

if [[ "${#expected_libraries[@]}" -eq 0 ]]; then
  echo "Linux native-library inventory is empty: $inventory" >&2
  exit 1
fi

if ! diff -u \
    <(printf '%s\n' "${expected_libraries[@]}") \
    <(printf '%s\n' "${actual_libraries[@]}"); then
  echo "Published native-library inventory does not match the reviewed x64 set" >&2
  exit 1
fi

echo "Validated ${#expected_libraries[@]} Linux native libraries in $publish_dir"

python3 "$script_dir/assert-linux-native-symbol-contracts.py" \
  "$publish_dir" \
  "$repository_root/src/Miningcore/Native" \
  "$inventory"
