#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 PUBLISH_DIRECTORY" >&2
  exit 64
fi

publish_dir=$(realpath "$1")
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
fixture="$repository_root/scripts/release/fixtures/ethash-light-vector.c"
native_root="$repository_root/src/Native"
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

common_expected='cbab5dd419e55a8b28935fe0a24fbe461c9b7393e850a0023db2f34bff84b499'
common_expected+=' 58a74dc1c38422f8009d8b73100472fe98e76f1f836c8b0640ad062a9031f576'
b3_expected='73e3fa4f01d0bfdeff9532dc048666d930f30a3f7b090a5aa5150ae33b020051'
b3_expected+=' 284e6a16dd3726022139202faac0f38068ed7c271db7e494e1c071112a526c16'

for family in ethhash etchash ethhashb3 ubqhash; do
  library="$publish_dir/lib${family}.so"

  if [[ ! -r "$library" ]]; then
    echo "Published Ethash-family library is missing: lib${family}.so" >&2
    exit 1
  fi

  cc -std=c11 -O2 -Wall -Wextra -Werror -Wno-unused-function \
    -iquote "$native_root/lib${family}" "$fixture" \
    -L"$publish_dir" -l"$family" -o "$work_dir/$family-vector"

  actual=$(LD_LIBRARY_PATH="$publish_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    "$work_dir/$family-vector")

  if [[ "$family" == ethhashb3 ]]; then
    expected=$b3_expected
  else
    expected=$common_expected
  fi

  if [[ "$actual" != "$expected" ]]; then
    echo "Published lib${family}.so failed its synthetic light-cache vector" >&2
    echo "Expected: $expected" >&2
    echo "Actual:   $actual" >&2
    exit 1
  fi
done

echo "Published Ethash-family libraries passed synthetic light-cache vectors"
