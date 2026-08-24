#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
native_root="$repository_root/src/Native/libmultihash"
fixture="$repository_root/scripts/release/fixtures/xelis-portable-aes-round.cpp"
audit="$repository_root/scripts/release/assert-warning-free-build.sh"
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

if g++ -dM -E -x c++ /dev/null | grep -Fq '__AES__'; then
  echo "Compiler unexpectedly enables AES without the requested feature flag" >&2
  exit 1
fi

common_flags=(
  -std=c++11
  -O2
  -Wall
  -Wextra
  -Werror
  -I"$native_root/xelishash"
)

g++ "${common_flags[@]}" -U__AES__ "$fixture" -o "$work_dir/portable-aes-round"
"$work_dir/portable-aes-round"

compile_log="$work_dir/xelishashv1-no-aes.log"
g++ -c -g -Wall -fPIC -fpermissive -O2 \
  -Wno-char-subscripts -Wno-unused-variable -Wno-unused-function \
  -Wno-strict-aliasing -Wno-sign-compare -std=c++11 -U__AES__ \
  "$native_root/xelishash/xelishashv1.cpp" \
  -o "$work_dir/xelishashv1-no-aes.o" 2>&1 | tee "$compile_log"

bash "$audit" "$compile_log"

if nm -u "$work_dir/xelishashv1-no-aes.o" | grep -Eq 'AES_(encrypt|set_encrypt_key)'; then
  echo "Xelis v1 non-AES object still depends on deprecated OpenSSL AES functions" >&2
  exit 1
fi

aesni_compared=0

if "$repository_root/src/Native/check_cpu.sh" aes; then
  g++ "${common_flags[@]}" -maes -DMININGCORE_TEST_AESNI \
    "$fixture" -o "$work_dir/hardware-aes-round"
  "$work_dir/hardware-aes-round"
  aesni_compared=1
else
  echo "Skipping Xelis v1 AES-NI equivalence: host CPU does not expose AES support"
fi

if [[ "$aesni_compared" -eq 1 ]]; then
  echo "Xelis v1 portable AES path is warning-free and matches the AES-NI round"
else
  echo "Xelis v1 portable AES path is warning-free and matches the reviewed vector"
fi
