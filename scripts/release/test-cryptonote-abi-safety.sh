#!/usr/bin/env bash

set -euo pipefail

publish_dir=${1:?usage: test-cryptonote-abi-safety.sh PUBLISH_DIRECTORY}
publish_dir=$(realpath "$publish_dir")
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
source_dir="$repository_root/src/Native/libcryptonote"
fixture="$repository_root/scripts/release/fixtures/cryptonote-abi-safety.cpp"
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

"${CXX:-c++}" \
  -std=c++14 \
  -Wall \
  -Wextra \
  -Werror \
  -Wno-class-memaccess \
  -Wno-unused-parameter \
  -I"$source_dir" \
  -I"$source_dir/contrib/epee/include" \
  "$fixture" \
  -L"$publish_dir" \
  -Wl,-rpath,"$publish_dir" \
  -lcryptonote \
  -lsodium \
  -o "$work_dir/cryptonote-abi-safety"

LD_LIBRARY_PATH="$publish_dir" timeout 10 "$work_dir/cryptonote-abi-safety"

echo 'CryptoNote native ABI safety test passed'
