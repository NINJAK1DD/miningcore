#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
"${CC:-cc}" -std=c11 -O2 -fno-fast-math -Wall -Wextra -Werror \
  "$script_dir/fixtures/native-load-fenv.c" -ldl -lm -o "$work_dir/probe"
cat > "$work_dir/library.c" <<'C'
#define _POSIX_C_SOURCE 200809L
#include <fenv.h>
#include <signal.h>
#include <unistd.h>
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
#ifdef FLUSH_CLEAR
__attribute__((constructor)) static void clear(void) {
    (void)fesetenv(FE_DFL_ENV);
}
#endif
#ifdef CRASH
__attribute__((constructor)) static void crash(void) { (void)raise(SIGSEGV); }
#endif
#ifdef HANG
__attribute__((constructor)) static void hang(void) {
#ifdef IGNORE_TERM
    (void)signal(SIGTERM, SIG_IGN);
#endif
    for(;;) pause();
}
#endif
C
for kind in clean STATUS_ONLY POISON DESTRUCTOR_POISON FLUSH_POISON FLUSH_CLEAR CRASH HANG HANG_IGNORE_TERM; do
  definitions=()
  if [[ "$kind" != clean ]]; then definitions+=("-D$kind"); fi
  if [[ "$kind" == HANG_IGNORE_TERM ]]; then definitions+=(-DHANG -DIGNORE_TERM); fi
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
       (native_supported_flush_bits() & 0x8000u) == 0)
        return 1;
#endif
    return 0;
}
C
"${CC:-cc}" -std=c11 -O2 -fno-fast-math -Wall -Wextra -Werror -I"$script_dir/fixtures" \
  "$work_dir/mask.c" -o "$work_dir/mask"
timeout --kill-after=2s 10s "$work_dir/mask"
run_probe() { timeout --kill-after=2s 10s "$work_dir/probe" "$@"; }
for mode in {0..7}; do run_probe "$work_dir/clean.so" "$mode"; done
run_probe "$work_dir/STATUS_ONLY.so" 0
for kind in POISON DESTRUCTOR_POISON FLUSH_POISON; do
  if run_probe "$work_dir/$kind.so" 0 >"$work_dir/failure.log" 2>&1; then
    echo "Probe accepted $kind floating-point corruption" >&2; exit 1
  fi
  grep -q 'changed FP state' "$work_dir/failure.log"
done
# Clearing controls is harmless in the default mode, but must fail with FTZ on.
run_probe "$work_dir/FLUSH_CLEAR.so" 0
if run_probe "$work_dir/FLUSH_CLEAR.so" 4 >"$work_dir/failure.log" 2>&1; then
  echo 'Probe accepted clearing enabled flush controls' >&2; exit 1
fi
grep -q 'changed FP state' "$work_dir/failure.log"
if run_probe "$work_dir/missing.so" 0 >"$work_dir/failure.log" 2>&1; then
  echo 'Probe accepted a missing native library' >&2; exit 1
fi
grep -q 'dlopen failed' "$work_dir/failure.log"
cp "$work_dir/clean.so" "$work_dir/libcryptonote.so"
run_probe "$work_dir/libcryptonote.so" 0
if run_probe "$work_dir/libcryptonote.so" 0 --cryptonote-vectors >"$work_dir/failure.log" 2>&1; then
  echo 'Vector mode accepted a symbol-only CryptoNote stub' >&2; exit 1
fi
grep -q 'missing cn_fast_hash_export' "$work_dir/failure.log"

# Exercise the real shell entry point with an isolated copy of its inventory.
# No production inventory/probe-path override is needed for these fixtures.
gate_dir="$work_dir/gate"
publish_dir="$work_dir/publish"
mkdir -p "$gate_dir/fixtures" "$publish_dir"
cp "$script_dir/test-native-load-fenv.sh" "$gate_dir/"
cp "$script_dir/fixtures/native-load-fenv.c" "$script_dir/fixtures/native-mxcsr.h" "$gate_dir/fixtures/"
cp "$work_dir/clean.so" "$publish_dir/libalpha.so"
cp "$work_dir/clean.so" "$publish_dir/libbeta.so"
inventory="$gate_dir/linux-native-libraries.txt"
run_gate() {
  MININGCORE_FENV_TIMEOUT_SECONDS=${MININGCORE_FENV_TIMEOUT_SECONDS:-1} timeout --kill-after=2s 20s \
    bash "$gate_dir/test-native-load-fenv.sh" "$@"
}
expect_gate_failure() {
  local expected_status=$1 pattern=$2 status=0
  shift 2
  run_gate "$@" >"$work_dir/gate.log" 2>&1 || status=$?
  if [[ "$status" != "$expected_status" ]] || ! grep -Fq -- "$pattern" "$work_dir/gate.log"; then
    cat "$work_dir/gate.log" >&2
    echo "Expected gate status $expected_status and diagnostic: $pattern; got $status" >&2
    exit 1
  fi
}
# The last entry must run even without a newline: check both success count and
# a poison last entry that the former read loop silently skipped.
printf 'libalpha.so\nlibbeta.so' > "$inventory"
run_gate "$publish_dir" >"$work_dir/gate.log" 2>&1
grep -q 'for 2 native libraries in eight modes' "$work_dir/gate.log"
cp "$work_dir/FLUSH_POISON.so" "$publish_dir/libbeta.so"
expect_gate_failure 1 'libbeta.so mode 0 (status 1)' "$publish_dir"
printf '%s\r\n' libalpha.so > "$inventory"
run_gate "$publish_dir" >"$work_dir/gate.log" 2>&1
grep -q 'for 1 native libraries in eight modes' "$work_dir/gate.log"

expect_gate_failure 64 'usage:'
expect_gate_failure 64 'usage:' "$publish_dir" --cryptonote-vectors extra
expect_gate_failure 64 'Unknown native floating-point probe option' "$publish_dir" --unknown
LD_LIBRARY_PATH="$publish_dir" expect_gate_failure 64 'Unset LD_LIBRARY_PATH' "$publish_dir"
LD_PRELOAD="$work_dir/clean.so" expect_gate_failure 64 'Unset LD_PRELOAD' "$publish_dir"
for invalid in 0 11 -1 1s; do
  MININGCORE_FENV_TIMEOUT_SECONDS="$invalid" expect_gate_failure 64 \
    'MININGCORE_FENV_TIMEOUT_SECONDS must be an integer from 1 to 10' "$publish_dir"
done
for invalid in '../libalpha.so' '' 'libalpha.so extra'; do
  printf '%s\n' "$invalid" > "$inventory"
  expect_gate_failure 1 'Invalid native inventory entry' "$publish_dir"
done
: > "$inventory"
expect_gate_failure 1 'Native inventory is empty' "$publish_dir"
printf '%s\n' libmissing.so > "$inventory"
expect_gate_failure 1 'Missing native library: libmissing.so' "$publish_dir"
printf '%s\n' libcryptonote.so > "$inventory"
cp "$work_dir/clean.so" "$publish_dir/libcryptonote.so"
expect_gate_failure 1 'missing cn_fast_hash_export' "$publish_dir" --cryptonote-vectors

# Both TERM and forced KILL must be bounded and report the library and mode.
# Disable core dumps from the deliberate crash; timeout owns each process group.
ulimit -c 0
for kind in CRASH HANG HANG_IGNORE_TERM; do
  cp "$work_dir/$kind.so" "$publish_dir/libalpha.so"
  printf '%s\n' libalpha.so > "$inventory"
  case "$kind" in
    CRASH) status=139 ;;
    HANG) status=124 ;;
    HANG_IGNORE_TERM) status=137 ;;
  esac
  expect_gate_failure 1 "libalpha.so mode 0 (status $status)" "$publish_dir"
done
echo 'Native floating-point loader regressions passed'
