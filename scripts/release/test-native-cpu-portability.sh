#!/usr/bin/env bash
set -euo pipefail

library_dir=$(realpath "${1:?usage: test-native-cpu-portability.sh LIBRARY_DIRECTORY [--avx2-userspace]}")
cpu=Nehalem-v1,+aes,+pclmulqdq
if [[ $# -eq 2 && $2 == --avx2-userspace ]]; then
  # The Ubuntu 26.04 hosted runner uses amd64v3 system libraries. Those
  # dependencies require AVX2 even though our own native baseline is lower.
  cpu=Haswell-v1
elif [[ $# -ne 1 ]]; then
  echo 'Expected LIBRARY_DIRECTORY [--avx2-userspace]' >&2
  exit 64
fi
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
ulimit -c 0

command -v qemu-x86_64 >/dev/null || {
  echo 'qemu-user is required (apt-get install qemu-user)' >&2
  exit 1
}
g++ -std=c++11 -O2 -fno-inline -fPIC -shared -march=x86-64 -Wall -Wextra -Werror \
  -I "$repository_root/src/Native/libdero/include" \
  "$repository_root/src/Native/libdero/include/highwayhash/instruction_sets.cc" \
  -o "$work_dir/libdispatch.so"
g++ -std=c++11 -O2 -march=x86-64 -Wall -Wextra -Werror \
  -I "$repository_root/src/Native/libdero/include" \
  "$repository_root/scripts/release/fixtures/highwayhash-osxsave.cpp" \
  -L "$work_dir" -ldispatch -Wl,--export-dynamic,-rpath,"$work_dir" -o "$work_dir/osxsave"
# Each process starts with an empty capability cache.
for state in {0..8}; do
  timeout 30 "$work_dir/osxsave" "$state"
done
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
timeout 30 qemu-x86_64 -cpu "$cpu" "$work_dir/unsupported" > "$work_dir/control.log" 2>&1
status=$?
set -e
if [[ $status -ne 132 ]]; then
  cat "$work_dir/control.log" >&2
  echo "AVX-512 negative control returned $status instead of SIGILL (132)" >&2
  exit 1
fi

if [[ $cpu == Nehalem-v1,* ]]; then
  cat > "$work_dir/unsupported-avx2.c" <<'C'
int main(void) { __asm__ volatile("vpbroadcastd %xmm0, %ymm0"); return 0; }
C
  gcc -march=x86-64 "$work_dir/unsupported-avx2.c" -o "$work_dir/unsupported-avx2"
  set +e
  timeout 30 qemu-x86_64 -cpu "$cpu" "$work_dir/unsupported-avx2" > "$work_dir/control-avx2.log" 2>&1
  status=$?
  set -e
  if [[ $status -ne 132 ]]; then
    cat "$work_dir/control-avx2.log" >&2
    echo "AVX2 negative control returned $status instead of SIGILL (132)" >&2
    exit 1
  fi
fi

export LD_LIBRARY_PATH="$library_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
echo 'Testing native runner CPU'
timeout 300 "$work_dir/probe" "$library_dir" | tee "$work_dir/native.log"
echo "Testing CPU portability with QEMU $cpu (no AVX-512)"
timeout 600 qemu-x86_64 -cpu "$cpu" "$work_dir/probe" "$library_dir" | tee "$work_dir/emulated.log"
diff -u "$work_dir/native.log" "$work_dir/emulated.log"
