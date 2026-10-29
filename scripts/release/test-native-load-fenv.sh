#!/usr/bin/env bash
set -euo pipefail
usage() {
  echo 'usage: test-native-load-fenv.sh PUBLISH_DIRECTORY [--cryptonote-vectors]' >&2
  exit 64
}
[[ "$#" -ge 1 && "$#" -le 2 ]] || usage
probe_args=()
case "${2:-}" in
  '') ;;
  --cryptonote-vectors) probe_args+=(--cryptonote-vectors) ;;
  *) echo 'Unknown native floating-point probe option' >&2; usage ;;
esac
for variable in LD_LIBRARY_PATH LD_PRELOAD; do
  if [[ -n "${!variable:-}" ]]; then
    echo "Unset $variable before running the native floating-point load gate" >&2
    exit 64
  fi
done
# Tests may shorten the deadline, but no caller may disable or extend it.
probe_seconds=${MININGCORE_FENV_TIMEOUT_SECONDS:-10}
[[ "$probe_seconds" =~ ^([1-9]|10)$ ]] || {
  echo 'MININGCORE_FENV_TIMEOUT_SECONDS must be an integer from 1 to 10' >&2
  exit 64
}
publish_dir=$(realpath -- "$1")
[[ -d "$publish_dir" ]] || { echo 'Native publish directory does not exist' >&2; exit 1; }
script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
"${CC:-cc}" -std=c11 -O2 -fno-fast-math -Wall -Wextra -Werror \
  "$script_dir/fixtures/native-load-fenv.c" -ldl -lm -o "$work_dir/probe"
count=0
while IFS= read -r library || [[ -n "$library" ]]; do
  library=${library%$'\r'}
  [[ "$library" =~ ^lib[a-z0-9]+\.so$ ]] || { echo 'Invalid native inventory entry' >&2; exit 1; }
  [[ -f "$publish_dir/$library" ]] || { echo "Missing native library: $library" >&2; exit 1; }
  for mode in {0..7}; do
    # A fresh process for each library/mode ensures constructors actually execute.
    LD_LIBRARY_PATH="$publish_dir" \
      timeout --kill-after=2s "${probe_seconds}s" "$work_dir/probe" "$publish_dir/$library" "$mode" "${probe_args[@]}" || {
        status=$?
        echo "Floating-point load probe failed for $library mode $mode (status $status)" >&2
        exit 1
      }
  done
  count=$((count + 1))
done < "$script_dir/linux-native-libraries.txt"
[[ "$count" -gt 0 ]] || { echo 'Native inventory is empty' >&2; exit 1; }
echo "Validated floating-point load/unload state for $count native libraries in eight modes"
