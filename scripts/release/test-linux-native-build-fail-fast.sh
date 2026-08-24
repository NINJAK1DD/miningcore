#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
driver="$repository_root/src/Miningcore/build-libs-linux.sh"
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

mkdir -p \
  "$work_dir/src/Miningcore" \
  "$work_dir/src/Native/libmultihash" \
  "$work_dir/src/Native/libbeamhash" \
  "$work_dir/bin" \
  "$work_dir/out"
: > "$work_dir/trace"

cp "$driver" "$work_dir/src/Miningcore/build-libs-linux.sh"

cat > "$work_dir/src/Native/check_cpu.sh" <<'SH'
#!/usr/bin/env sh
exit 1
SH
chmod +x "$work_dir/src/Native/check_cpu.sh"

cat > "$work_dir/bin/make" <<'SH'
#!/usr/bin/env sh
set -eu

printf '%s %s\n' "$(basename "$PWD")" "$*" >> "$MININGCORE_NATIVE_TEST_TRACE"

if [ "$(basename "$PWD")" = libmultihash ] && [ "${1:-}" != clean ]; then
  exit 42
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

set +e
(
  cd "$work_dir/src/Miningcore"
  PATH="$work_dir/bin:$PATH" \
    MININGCORE_NATIVE_TEST_TRACE="$work_dir/trace" \
    bash build-libs-linux.sh "$work_dir/out"
) > "$work_dir/output" 2>&1
status=$?
set -e

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

echo "Native build driver stopped at the injected first-component failure"
