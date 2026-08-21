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
counter_file="$MININGCORE_HELPER_TEST_STATE/sudo"
counter=0

if [ -f "$counter_file" ]; then
  counter=$(cat "$counter_file")
fi

counter=$((counter + 1))
printf '%s\n' "$counter" > "$counter_file"

if [ "${MININGCORE_HELPER_FAIL_TOOL:-}" = sudo ] &&
    [ "${MININGCORE_HELPER_FAIL_CALL:-0}" -eq "$counter" ]; then
  exit 42
fi

"$@"
SH

cat > "$work_dir/bin/wget" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'wget %s\n' "$*" >> "$MININGCORE_HELPER_TEST_TRACE"
counter_file="$MININGCORE_HELPER_TEST_STATE/wget"
counter=0

if [ -f "$counter_file" ]; then
  counter=$(cat "$counter_file")
fi

counter=$((counter + 1))
printf '%s\n' "$counter" > "$counter_file"

if [ "${MININGCORE_HELPER_FAIL_TOOL:-}" = wget ] &&
    [ "${MININGCORE_HELPER_FAIL_CALL:-0}" -eq "$counter" ]; then
  exit 42
fi

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

cat > "$work_dir/bin/rm" <<'SH'
#!/usr/bin/env sh
set -eu

printf 'rm %s\n' "$*" >> "$MININGCORE_HELPER_TEST_TRACE"
counter_file="$MININGCORE_HELPER_TEST_STATE/rm"
counter=0

if [ -f "$counter_file" ]; then
  counter=$(cat "$counter_file")
fi

counter=$((counter + 1))
printf '%s\n' "$counter" > "$counter_file"

if [ "${MININGCORE_HELPER_FAIL_TOOL:-}" = rm ] &&
    [ "${MININGCORE_HELPER_FAIL_CALL:-0}" -eq "$counter" ]; then
  exit 42
fi

exec /bin/rm "$@"
SH

for tool in apt-get add-apt-repository dpkg; do
  cat > "$work_dir/bin/$tool" <<'SH'
#!/usr/bin/env sh
set -eu

printf '%s %s\n' "${0##*/}" "$*" >> "$MININGCORE_HELPER_TEST_TRACE"
SH
done

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

assert_fail_closed() {
  local helper=$1
  local sandbox=$2
  local publish_dir=$3
  local trace=$4
  local state=$5
  local output=$6
  local fail_tool=$7
  local fail_call=$8
  local status

  : > "$trace"
  /bin/rm -f -- "$state"/*

  set +e
  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_HELPER_FAIL_TOOL="$fail_tool" \
      MININGCORE_HELPER_FAIL_CALL="$fail_call" \
      MININGCORE_HELPER_TEST_STATE="$state" \
      MININGCORE_HELPER_TEST_TRACE="$trace" \
      bash "$helper" "$publish_dir"
  ) > "$output" 2>&1
  status=$?
  set -e

  if [[ "$status" -eq 0 ]]; then
    echo "$helper reported success after $fail_tool call $fail_call failed" >&2
    cat "$output" >&2
    exit 1
  fi

  if grep -Fq 'dotnet publish ' "$trace"; then
    echo "$helper attempted to publish after $fail_tool call $fail_call failed" >&2
    cat "$trace" >&2
    exit 1
  fi
}

for helper in "${helpers[@]}"; do
  sandbox="$work_dir/${helper%.sh}"
  trace="$sandbox/trace"
  output="$sandbox/output"
  publish_dir="$sandbox/published"
  state="$sandbox/state"

  mkdir -p "$sandbox/src/Miningcore" "$sandbox/scripts/release" "$state"
  cp "$repository_root/$helper" "$sandbox/$helper"
  cp "$repository_root/scripts/release/source-build-identity.sh" \
    "$sandbox/scripts/release/source-build-identity.sh"

  bash -n "$sandbox/$helper"

  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_HELPER_TEST_STATE="$state" \
      MININGCORE_HELPER_TEST_TRACE="$trace" \
      bash "$helper" "$publish_dir"
  ) > "$output" 2>&1

  grep -Fq "dotnet publish -c Release --framework net10.0 -o $publish_dir" "$trace"
  sudo_count=$(grep -c '^sudo ' "$trace")

  for ((fail_call = 1; fail_call <= sudo_count; fail_call++)); do
    assert_fail_closed "$helper" "$sandbox" "$publish_dir" "$trace" "$state" \
      "$output" sudo "$fail_call"
  done

  if [[ "$helper" == build-debian-12.sh ]]; then
    assert_fail_closed "$helper" "$sandbox" "$publish_dir" "$trace" "$state" \
      "$output" wget 1
    assert_fail_closed "$helper" "$sandbox" "$publish_dir" "$trace" "$state" \
      "$output" rm 1
  fi
done

echo "Source-build helpers passed hermetic success and fail-closed checks"
