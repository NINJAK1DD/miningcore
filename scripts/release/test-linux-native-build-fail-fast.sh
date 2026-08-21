#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
driver="$repository_root/src/Miningcore/build-libs-linux.sh"
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

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
