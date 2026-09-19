#!/usr/bin/env bash
set -euo pipefail
source_root=${1:?usage: test-randomx-cpu-os-state.sh SOURCE_ROOT}
for fork in RandomX RandomARQ Panthera RandomXSCash; do
  for source in cpu.cpp cpu.hpp; do
    if [[ ! -f "$source_root/$fork/src/$source" ]]; then
      echo "Missing $source_root/$fork/src/$source; run the native build first and pass its TMPDIR as SOURCE_ROOT" >&2
      exit 1
    fi
  done
done
source_root=$(realpath "$source_root")
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
for fork in RandomX RandomARQ Panthera RandomXSCash; do
  # Keep hardware-read calls interposable; the constructor and its decision
  # logic are compiled unchanged from each actual pinned/patched fork.
  g++ -std=c++11 -O2 -fno-inline -fPIC -shared -march=x86-64 -Wall -Wextra -Werror \
    "$source_root/$fork/src/cpu.cpp" -o "$work_dir/libcpu.so"
  g++ -std=c++11 -O2 -march=x86-64 -Wall -Wextra -Werror \
    -I "$source_root/$fork/src" "$repository_root/scripts/release/fixtures/randomx-cpu-os-state.cpp" \
    -L "$work_dir" -lcpu -Wl,--export-dynamic,-rpath,"$work_dir" -o "$work_dir/cpu"
  echo "Testing $fork with controlled CPUID and XCR0"
  timeout 30 "$work_dir/cpu"
done
echo 'All four RandomX CPU detectors enforce CPU and OS AVX state'
