#!/usr/bin/env bash
set -euo pipefail

library_dir=$(realpath "${1:?usage: test-native-cpu-portability.sh LIBRARY_DIRECTORY}")
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
ulimit -c 0

command -v qemu-x86_64 >/dev/null
# Build the probe without optional instructions; test the actual shipped DSOs.
g++ -std=c++11 -O2 -march=x86-64 -Wall -Wextra -Werror \
  "$repository_root/scripts/release/fixtures/native-cpu-portability.cpp" \
  -ldl -o "$work_dir/probe"

# A negative control proves the emulator rejects AVX-512 even on an AVX-512
# runner. A version/configuration that silently accepts it must fail this gate.
cat > "$work_dir/unsupported.c" <<'C'
int main(void) { __asm__ volatile("vpxord %zmm0, %zmm0, %zmm0"); return 0; }
C
gcc -march=x86-64 "$work_dir/unsupported.c" -o "$work_dir/unsupported"
set +e
qemu-x86_64 -cpu Nehalem-v1,+aes,+pclmulqdq "$work_dir/unsupported" > "$work_dir/control.log" 2>&1
status=$?
set -e
if [[ $status -ne 132 ]]; then
  cat "$work_dir/control.log" >&2
  echo "AVX-512 negative control returned $status instead of SIGILL (132)" >&2
  exit 1
fi

export LD_LIBRARY_PATH="$library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
echo 'Testing native runner CPU'
timeout 180 "$work_dir/probe" "$library_dir" | tee "$work_dir/native.log"
echo 'Testing the baseline without AVX/AVX2/AVX-512 (QEMU Nehalem + AES/PCLMUL)'
timeout 300 qemu-x86_64 -cpu Nehalem-v1,+aes,+pclmulqdq "$work_dir/probe" "$library_dir" | tee "$work_dir/emulated.log"
diff -u "$work_dir/native.log" "$work_dir/emulated.log"
echo 'Testing HighwayHash without OS XSAVE support'
timeout 30 qemu-x86_64 -cpu Haswell-v1,-xsave "$work_dir/probe" "$library_dir" --highway-only
