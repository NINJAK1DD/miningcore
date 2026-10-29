#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
"${CC:-cc}" -std=c11 -O2 -fno-fast-math -Wall -Wextra -Werror \
  "$script_dir/fixtures/native-load-fenv.c" -ldl -lm -o "$work_dir/probe"
cat > "$work_dir/library.c" <<'C'
#include <fenv.h>
#include "native-mxcsr.h"
int exported(void) { return 1; }
#ifdef POISON
__attribute__((constructor)) static void poison(void) { (void)fesetround(FE_DOWNWARD); }
#endif
#ifdef STATUS_ONLY
__attribute__((constructor)) static void status(void) { (void)feraiseexcept(FE_INEXACT); }
#endif
#ifdef DESTRUCTOR_POISON
__attribute__((destructor)) static void poison(void) { (void)fesetround(FE_UPWARD); }
#endif
#ifdef FLUSH_POISON
__attribute__((constructor)) static void poison(void) {
#if defined(__x86_64__)
    unsigned int value;
    __asm__ volatile("stmxcsr %0" : "=m"(value));
    value |= native_supported_flush_bits();
    __asm__ volatile("ldmxcsr %0" : : "m"(value));
#elif defined(__aarch64__)
    unsigned long value;
    __asm__ volatile("mrs %0, fpcr" : "=r"(value));
    value |= 1ul << 24;
    __asm__ volatile("msr fpcr, %0" : : "r"(value));
#endif
}
#endif
C
for kind in clean STATUS_ONLY POISON DESTRUCTOR_POISON FLUSH_POISON; do
  definitions=()
  if [[ "$kind" != clean ]]; then definitions+=("-D$kind"); fi
  "${CC:-cc}" -shared -fPIC -fno-fast-math -I"$script_dir/fixtures" "${definitions[@]}" \
    "$work_dir/library.c" -lm -o "$work_dir/$kind.so"
done
cat > "$work_dir/mask.c" <<'C'
#include "native-mxcsr.h"
int main(void) {
#if defined(__x86_64__)
    if(native_mxcsr_flush_bits(0) != 0x8000u ||
       native_mxcsr_flush_bits(0xffbfu) != 0x8000u ||
       native_mxcsr_flush_bits(0xffffu) != 0x8040u ||
       native_mxcsr_flush_bits(0x40u) != 0x40u ||
       (native_supported_flush_bits() & ~0x8040u) != 0)
        return 1;
#endif
    return 0;
}
C
"${CC:-cc}" -std=c11 -O2 -fno-fast-math -Wall -Wextra -Werror -I"$script_dir/fixtures" \
  "$work_dir/mask.c" -o "$work_dir/mask"
"$work_dir/mask"
for mode in {0..7}; do "$work_dir/probe" "$work_dir/clean.so" "$mode"; done
"$work_dir/probe" "$work_dir/STATUS_ONLY.so" 0
for kind in POISON DESTRUCTOR_POISON FLUSH_POISON; do
  if "$work_dir/probe" "$work_dir/$kind.so" 0 >"$work_dir/failure.log" 2>&1; then
    echo "Probe accepted $kind floating-point corruption" >&2; exit 1
  fi
  grep -q 'changed FP state' "$work_dir/failure.log"
done
if "$work_dir/probe" "$work_dir/missing.so" 0 >"$work_dir/failure.log" 2>&1; then
  echo 'Probe accepted a missing native library' >&2; exit 1
fi
grep -q 'dlopen failed' "$work_dir/failure.log"
cp "$work_dir/clean.so" "$work_dir/libcryptonote.so"
"$work_dir/probe" "$work_dir/libcryptonote.so" 0
if "$work_dir/probe" "$work_dir/libcryptonote.so" 0 --cryptonote-vectors >"$work_dir/failure.log" 2>&1; then
  echo 'Vector mode accepted a symbol-only CryptoNote stub' >&2; exit 1
fi
grep -q 'missing cn_fast_hash_export' "$work_dir/failure.log"
echo 'Native floating-point loader regressions passed'
