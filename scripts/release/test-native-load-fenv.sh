#!/usr/bin/env bash
set -euo pipefail
publish_dir=$(realpath "${1:?usage: test-native-load-fenv.sh PUBLISH_DIRECTORY}")
script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
"${CC:-cc}" -std=c11 -O2 -fno-fast-math -Wall -Wextra -Werror \
  "$script_dir/fixtures/native-load-fenv.c" -ldl -lm -o "$work_dir/probe"
count=0
while IFS= read -r library; do
  library=${library%$'\r'}
  [[ "$library" =~ ^lib[a-z0-9]+\.so$ ]] || { echo 'Invalid native inventory entry' >&2; exit 1; }
  [[ -f "$publish_dir/$library" ]] || { echo "Missing native library: $library" >&2; exit 1; }
  for mode in {0..7}; do
    # A fresh process for each library/mode ensures constructors actually execute.
    LD_LIBRARY_PATH="$publish_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
      timeout --kill-after=2s 10s "$work_dir/probe" "$publish_dir/$library" "$mode"
  done
  count=$((count + 1))
done < "$script_dir/linux-native-libraries.txt"
[[ "$count" -gt 0 ]]
echo "Validated floating-point load/unload state for $count native libraries in eight modes"
