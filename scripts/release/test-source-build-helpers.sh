#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

mkdir -p "$work_dir/bin"

cat > "$work_dir/bin/sudo" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'sudo %s\n' "$*" >> "$MININGCORE_HELPER_TEST_TRACE"

if [ "${MININGCORE_HELPER_FAIL_SUDO:-0}" = 1 ]; then
  exit 42
fi
SH

cat > "$work_dir/bin/wget" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'wget %s\n' "$*" >> "$MININGCORE_HELPER_TEST_TRACE"
output=

while [ "$#" -gt 0 ]; do
  if [ "$1" = -O ]; then
    output=$2
    shift 2
  else
    shift
  fi
done

test -n "$output"
: > "$output"
SH

cat > "$work_dir/bin/dpkg" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'dpkg %s\n' "$*" >> "$MININGCORE_HELPER_TEST_TRACE"
SH

cat > "$work_dir/bin/git" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'git %s\n' "$*" >> "$MININGCORE_HELPER_TEST_TRACE"
exit 1
SH

cat > "$work_dir/bin/dotnet" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'dotnet %s\n' "$*" >> "$MININGCORE_HELPER_TEST_TRACE"
test "${1:-}" = publish
SH

chmod +x "$work_dir/bin/"*

helpers=(
  build-debian-12.sh
  build-ubuntu-22.04.sh
  build-ubuntu-24.04.sh
)

for helper in "${helpers[@]}"; do
  sandbox="$work_dir/${helper%.sh}"
  trace="$sandbox/trace"
  output="$sandbox/output"
  publish_dir="$sandbox/published"

  mkdir -p "$sandbox/src/Miningcore" "$sandbox/scripts/release"
  cp "$repository_root/$helper" "$sandbox/$helper"
  cp "$repository_root/scripts/release/source-build-identity.sh" \
    "$sandbox/scripts/release/source-build-identity.sh"

  bash -n "$sandbox/$helper"

  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_HELPER_TEST_TRACE="$trace" \
      bash "$helper" "$publish_dir"
  ) > "$output" 2>&1

  grep -Fq "dotnet publish -c Release --framework net10.0 -o $publish_dir" "$trace"

  : > "$trace"
  set +e
  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_HELPER_FAIL_SUDO=1 \
      MININGCORE_HELPER_TEST_TRACE="$trace" \
      bash "$helper" "$publish_dir"
  ) > "$output" 2>&1
  status=$?
  set -e

  if [[ "$status" -eq 0 ]]; then
    echo "$helper reported success after its package-manager command failed" >&2
    cat "$output" >&2
    exit 1
  fi

  if grep -Fq 'dotnet publish ' "$trace"; then
    echo "$helper attempted to publish after its package-manager command failed" >&2
    cat "$trace" >&2
    exit 1
  fi
done

echo "Source-build helpers passed hermetic success and fail-closed checks"
