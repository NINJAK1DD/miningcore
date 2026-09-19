#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
driver="$repository_root/src/Miningcore/build-libs-linux.sh"
source_verifier="$repository_root/scripts/release/verify-pinned-source-files.sh"
randomx_patch="$repository_root/src/Miningcore/patches/randomx-cmake-policy-floor.patch"
randomarq_patch="$repository_root/src/Miningcore/patches/randomarq-cmake-policy-floor.patch"
panthera_patch="$repository_root/src/Miningcore/patches/panthera-build-status-warnings.patch"
randomxscash_patch="$repository_root/src/Miningcore/patches/randomxscash-cmake-policy-floor.patch"
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

for fixture in randomx randomarq panthera randomxscash; do
  mkdir -p "$work_dir/$fixture"
done

: > "$work_dir/randomx/CMakeLists.txt"
for ((line = 1; line <= 26; line++)); do
  printf '# fixture context %d\n' "$line" >> "$work_dir/randomx/CMakeLists.txt"
done
cat >> "$work_dir/randomx/CMakeLists.txt" <<'CMAKE'
# THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

cmake_minimum_required(VERSION 3.5)

project(RandomX)
CMAKE

: > "$work_dir/randomarq/CMakeLists.txt"
for ((line = 1; line <= 25; line++)); do
  printf '# fixture context %d\n' "$line" >> "$work_dir/randomarq/CMakeLists.txt"
done
cat >> "$work_dir/randomarq/CMakeLists.txt" <<'CMAKE'
# INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
# STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
# THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

cmake_minimum_required(VERSION 3.7)
message(STATUS "CMake version ${CMAKE_VERSION}")

CMAKE

cp "$work_dir/randomx/CMakeLists.txt" "$work_dir/panthera/CMakeLists.txt"
cp "$work_dir/randomx/CMakeLists.txt" "$work_dir/randomxscash/CMakeLists.txt"

mkdir -p "$work_dir/panthera/src/yespower"
cat > "$work_dir/panthera/src/yespower/yespower-opt.c" <<'C'
 * no slowdown from the prefixes is generally observed on AMD CPUs supporting
 * XOP, some slowdown is sometimes observed on Intel CPUs with AVX.
 */
#ifdef __XOP__
#warning "Note: XOP is enabled.  That's great."
#elif defined(__AVX__)
#warning "Note: AVX is enabled.  That's OK."
#elif defined(__SSE2__)
#warning "Note: AVX and XOP are not enabled.  That's OK."
#elif defined(__x86_64__) || defined(__i386__)
#warning "SSE2 not enabled.  Expect poor performance."
#else
#warning "Note: building generic code for non-x86.  That's OK."
#endif

/*
 * The SSE4 code version has fewer instructions than the generic SSE2 version,
/* 64-bit without AVX.  This relies on out-of-order execution and register
 * renaming.  It may actually be fastest on CPUs with AVX(2) as well - e.g.,
 * it runs great on Haswell. */
#warning "Note: using x86-64 inline assembly for pwxform.  That's great."
#undef MAYBE_MEMORY_BARRIER
#define MAYBE_MEMORY_BARRIER \
	__asm__("" : : : "memory");
C

: > "$work_dir/panthera/src/yespower/sha256.c"
for ((line = 1; line <= 386; line++)); do
  printf '/* fixture context %d */\n' "$line" \
    >> "$work_dir/panthera/src/yespower/sha256.c"
done
cat >> "$work_dir/panthera/src/yespower/sha256.c" <<'C'
	memset(pad, 0x36, 64);
	for (i = 0; i < Klen; i++)
		pad[i] ^= K[i];
	_SHA256_Update(&ctx->ictx, pad, 64, tmp32);

	/* Outer SHA256 operation is SHA256(K xor [block of 0x5c] || hash). */
	SHA256_Init(&ctx->octx);
	memset(pad, 0x5c, 64);
	for (i = 0; i < Klen; i++)
		pad[i] ^= K[i];
	_SHA256_Update(&ctx->octx, pad, 64, tmp32);
}
C

for fixture in randomx randomarq randomxscash; do
  (
    cd "$work_dir/$fixture"
    sha256sum CMakeLists.txt > "$work_dir/$fixture.sha256"
  )
  bash "$source_verifier" "$work_dir/$fixture" "$work_dir/$fixture.sha256"
done

(
  cd "$work_dir/panthera"
  sha256sum CMakeLists.txt src/yespower/yespower-opt.c \
    src/yespower/sha256.c > "$work_dir/panthera.sha256"
)
bash "$source_verifier" "$work_dir/panthera" "$work_dir/panthera.sha256"

cp "$work_dir/randomx/CMakeLists.txt" "$work_dir/randomx/CMakeLists.txt.lf"
sed 's/$/\r/' "$work_dir/randomx/CMakeLists.txt.lf" \
  > "$work_dir/randomx/CMakeLists.txt"
sed 's/$/\r/' "$work_dir/randomx.sha256" \
  > "$work_dir/randomx-crlf.sha256"
bash "$source_verifier" "$work_dir/randomx" \
  "$work_dir/randomx-crlf.sha256"
mv "$work_dir/randomx/CMakeLists.txt.lf" \
  "$work_dir/randomx/CMakeLists.txt"

mkdir "$work_dir/manifest-directory"
set +e
bash "$source_verifier" "$work_dir/randomx" "$work_dir/manifest-directory" \
  > "$work_dir/manifest-directory-output" 2>&1
manifest_directory_status=$?
set -e

if [[ "$manifest_directory_status" -ne 70 ]]; then
  echo "Pinned source verifier accepted a directory as its manifest" >&2
  cat "$work_dir/manifest-directory-output" >&2
  exit 1
fi

cp "$work_dir/randomx/CMakeLists.txt" "$work_dir/randomx/CMakeLists.txt.clean"
printf '%s\n' '# injected upstream drift' >> "$work_dir/randomx/CMakeLists.txt"

set +e
bash "$source_verifier" "$work_dir/randomx" "$work_dir/randomx.sha256" \
  > "$work_dir/source-drift-output" 2>&1
source_drift_status=$?
set -e

if [[ "$source_drift_status" -ne 1 ]]; then
  echo "Pinned source verifier accepted modified upstream content" >&2
  cat "$work_dir/source-drift-output" >&2
  exit 1
fi

mv "$work_dir/randomx/CMakeLists.txt.clean" "$work_dir/randomx/CMakeLists.txt"

# These minimal fixtures validate only that each patch still applies and has the intended shape.
# The SHA-256 manifests above are the authoritative upstream-source drift boundary.
(
  cd "$work_dir/randomx"
  git apply --check "$randomx_patch"
  git apply "$randomx_patch"
)

(
  cd "$work_dir/randomarq"
  git apply --check "$randomarq_patch"
  git apply "$randomarq_patch"
)

(
  cd "$work_dir/panthera"
  git apply --check "$panthera_patch"
  git apply "$panthera_patch"
)

(
  cd "$work_dir/randomxscash"
  git apply --check "$randomxscash_patch"
  git apply "$randomxscash_patch"
)

for fixture in randomx randomarq panthera randomxscash; do
  if ! grep -Fxq 'cmake_minimum_required(VERSION 3.10)' \
      "$work_dir/$fixture/CMakeLists.txt"; then
    echo "$fixture source patch did not set the supported CMake policy floor" >&2
    exit 1
  fi
done

if grep -n '^[[:space:]]*#warning' \
    "$work_dir/panthera/src/yespower/yespower-opt.c"; then
  echo "Panthera source patch left an active build warning" >&2
  exit 1
fi

if [[ $(grep -Fxc $'\tfor (i = 0; i < Klen && i < 64; i++)' \
      "$work_dir/panthera/src/yespower/sha256.c") -ne 2 ]]; then
  echo "Panthera source patch did not bound both HMAC pad loops" >&2
  exit 1
fi

mkdir -p \
  "$work_dir/scripts/release" \
  "$work_dir/src/Miningcore" \
  "$work_dir/src/Native/libodocrypt" \
  "$work_dir/src/Native/libmultihash" \
  "$work_dir/src/Native/libbeamhash" \
  "$work_dir/bin" \
  "$work_dir/out"
: > "$work_dir/trace"

cp "$driver" "$work_dir/src/Miningcore/build-libs-linux.sh"
cp "$source_verifier" \
  "$work_dir/scripts/release/verify-pinned-source-files.sh"
cp "$repository_root/src/Native/libodocrypt/upstream.sha256" \
  "$work_dir/src/Native/libodocrypt/upstream.sha256"
for pinned_source in \
    odocrypt.cpp odocrypt.h KeccakP-800-reference.c KeccakP-800-SnP.h \
    brg_endian.h; do
  cp "$repository_root/src/Native/libmultihash/$pinned_source" \
    "$work_dir/src/Native/libmultihash/$pinned_source"
done

cat > "$work_dir/src/Native/check_cpu.sh" <<'SH'
#!/usr/bin/env sh
# CPU probing must never participate in the distributed ISA policy.
echo 'unexpected-cpu-probe' >> "$MININGCORE_NATIVE_TEST_TRACE"
exit 98
SH
chmod +x "$work_dir/src/Native/check_cpu.sh"

real_make=$(command -v make)
cat > "$work_dir/src/Native/libmultihash/Makefile" <<'MAKE'
.PHONY: all clean
all:
	@echo reviewed-makefile-all >> "$(MININGCORE_NATIVE_TEST_TRACE)"
	@exit 42
clean:
	@echo reviewed-makefile-clean >> "$(MININGCORE_NATIVE_TEST_TRACE)"
MAKE

cat > "$work_dir/bin/make" <<'SH'
#!/usr/bin/env sh
set -eu

printf '%s %s\n' "$(basename "$PWD")" "$*" >> "$MININGCORE_NATIVE_TEST_TRACE"

if [ "$(basename "$PWD")" = libmultihash ]; then
  exec "$MININGCORE_NATIVE_TEST_MAKE" "$@"
fi

touch "lib$(basename "$PWD" | sed 's/^lib//').so"
SH
chmod +x "$work_dir/bin/make"

for tool in git cmake ninja; do
  cat > "$work_dir/bin/$tool" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'unexpected-tool %s\n' "$(basename "$0")" >> "$MININGCORE_NATIVE_TEST_TRACE"
echo "Unexpected external build-tool invocation: $(basename "$0")" >&2
exit 97
SH
  chmod +x "$work_dir/bin/$tool"
done

# No inherited compiler/preprocessor/linker flags or Make precedence controls
# may reach even the first native tool. Include compiler-command flag injection.
for inherited in CFLAGS=-mavx512f CXXFLAGS=-mfma CPPFLAGS=-DHAVE_AVX512F \
    ASFLAGS=-march=skylake-avx512 LDFLAGS=-mavx512f LDLIBS=-mavx2 \
    'MAKEFLAGS=-e CFLAGS=-mavx512f' MFLAGS=-e GNUMAKEFLAGS=-e \
    MAKEOVERRIDES=CXXFLAGS=-mbmi2 MAKEFILES=/untrusted.mk \
    'CC=gcc -mavx512f' 'CXX=g++ -mavx2' 'CPP=gcc -E -mavx2' \
    AS=untrusted LD=untrusted AR=untrusted RANLIB=untrusted \
    CMAKE_TOOLCHAIN_FILE=/untrusted.cmake; do
  set +e
  env "$inherited" PATH="$work_dir/bin:$PATH" \
    MININGCORE_NATIVE_TEST_TRACE="$work_dir/trace" \
    bash "$work_dir/src/Miningcore/build-libs-linux.sh" "$work_dir/out" \
    > "$work_dir/rejected-output" 2>&1
  rejected_status=$?
  set -e
  if [[ "$rejected_status" -ne 64 || -s "$work_dir/trace" ]] ||
      ! grep -Fq "rejects inherited ${inherited%%=*}; unset it" "$work_dir/rejected-output"; then
    echo "Native driver did not reject ${inherited%%=*} before invoking tools" >&2
    cat "$work_dir/rejected-output" >&2
    exit 1
  fi
done

run_fixture_driver() {
  : > "$work_dir/trace"
  set +e
  (
    cd "$work_dir/src/Miningcore"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_NATIVE_TEST_MAKE="$real_make" \
      MININGCORE_NATIVE_TEST_TRACE="$work_dir/trace" \
      bash "${1:-build-libs-linux.sh}" "$work_dir/out"
  ) > "$work_dir/output" 2>&1
  status=$?
  set -e
}

check_driver_failure() {
  run_fixture_driver
  if [[ "$status" -eq 0 ]]; then
    echo "Native build driver reported success after the injected component failure" >&2
    cat "$work_dir/output" >&2
    exit 1
  fi

  if grep -Fq 'libbeamhash' "$work_dir/trace"; then
    echo "Native build driver attempted a later component after the injected failure" >&2
    cat "$work_dir/trace" >&2
    exit 1
  fi

  if grep -Fq 'unexpected-tool ' "$work_dir/trace"; then
    echo "Native build regression test unexpectedly reached an external build tool" >&2
    cat "$work_dir/trace" >&2
    exit 1
  fi

  if ! grep -Fq 'Building native component: libmultihash' "$work_dir/output"; then
    echo "Native build driver did not reach the injected first component" >&2
    cat "$work_dir/output" >&2
    exit 1
  fi

  for phase in clean all; do
    if ! grep -Fxq "reviewed-makefile-$phase" "$work_dir/trace"; then
      echo "GNU Make did not execute the reviewed Makefile's $phase target" >&2
      cat "$work_dir/output" >&2
      exit 1
    fi
  done
  if ! grep -Fq -- 'libmultihash -f Makefile CPU_FLAGS=-march=x86-64-v2 -mtune=generic -maes -mpclmul' "$work_dir/trace" ||
      grep -Eq -- '^libmultihash .*(-mavx|-march=native|HAVE_AVX)|unexpected-cpu-probe' "$work_dir/trace"; then
    echo "Native build inherited optional CPU capabilities from its builder" >&2
    cat "$work_dir/trace" >&2
    exit 1
  fi
}

check_driver_failure
# Mutate only a temporary copy, retaining the same directory and dependencies.
# This control proves rejection comes from GNU Make parsing the shadow file,
# rather than the assertion about the current driver's argument spelling.
sed 's/make -f Makefile/make/g' "$driver" \
  > "$work_dir/src/Miningcore/build-libs-implicit.sh"
if cmp -s "$driver" "$work_dir/src/Miningcore/build-libs-implicit.sh"; then
  echo 'Negative control did not remove explicit Makefile selection' >&2
  exit 1
fi
# Exercise each default-search shadow independently with real GNU Make. An
# implicit invocation fails at parse time before the reviewed recipes run.
for shadow in GNUmakefile makefile; do
  # shellcheck disable=SC2016 # Literal GNU Make parse-time error, not shell syntax.
  printf '%s\n' '$(error unreviewed shadow Makefile selected)' \
    > "$work_dir/src/Native/libmultihash/$shadow"
  check_driver_failure
  run_fixture_driver build-libs-implicit.sh
  if [[ "$status" -ne 2 || $(wc -l < "$work_dir/trace") -ne 1 ]] ||
      ! grep -Fxq 'libmultihash clean' "$work_dir/trace" ||
      ! grep -Fq "$shadow:1: *** unreviewed shadow Makefile selected." "$work_dir/output"; then
    echo "Implicit Makefile negative control did not fail on $shadow before running recipes" >&2
    cat "$work_dir/output" "$work_dir/trace" >&2
    exit 1
  fi
  echo "Native Make shadow negative control passed: $shadow"
  rm -- "$work_dir/src/Native/libmultihash/$shadow"
done
echo "Native build driver selected the reviewed Makefile and stopped at its injected failure (both shadow names tested)"
