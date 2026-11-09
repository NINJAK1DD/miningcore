#!/usr/bin/env bash
set -euo pipefail
library_dir=$(realpath "${1:?usage: test-blake2b-key-contract.sh LIBRARY_DIRECTORY}")
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
ulimit -c 0
export LD_LIBRARY_PATH="$library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
g++ -std=c++11 -O2 -march=x86-64 -Wall -Wextra -Werror \
  "$repository_root/scripts/release/fixtures/blake2b-key-contract.cpp" \
  -ldl -o "$work_dir/probe"
timeout 30 "$work_dir/probe" "$library_dir/libmultihash.so"
