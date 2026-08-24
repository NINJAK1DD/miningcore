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
resolved_command=

if [ -f "$counter_file" ]; then
  counter=$(cat "$counter_file")
fi

counter=$((counter + 1))
printf '%s\n' "$counter" > "$counter_file"

if [ "${MININGCORE_HELPER_FAIL_TOOL:-}" = sudo ] &&
    [ "${MININGCORE_HELPER_FAIL_CALL:-0}" -eq "$counter" ]; then
  exit 42
fi

resolved_command=$(command -v "$1" 2>/dev/null || true)

case "$resolved_command" in
  "$MININGCORE_HELPER_TEST_BIN"/*) ;;
  *)
    printf 'unstubbed-privileged %s\n' "$1" >> "$MININGCORE_HELPER_TEST_TRACE"
    echo "Un-stubbed privileged command in helper test: $1" >&2
    exit 90
    ;;
esac

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

case "${1:-}" in
  --list-sdks)
    printf '%s\n' '10.0.100 [/usr/lib/dotnet/sdk]'
    ;;
  --info)
    printf '%s\n' '.NET SDK 10.0.100 (Ubuntu fixture)'
    ;;
  publish)
    if [ "${LC_ALL:-}" != C ] || [ "${DOTNET_CLI_UI_LANGUAGE:-}" != en ]; then
      echo 'dotnet publish was not forced to stable English diagnostics' >&2
      exit 91
    fi
    if [ "${MININGCORE_HELPER_EMIT_WARNING:-}" = 1 ]; then
      echo 'fixture.c:1:1: warning: injected compiler warning'
    fi
    ;;
  *)
    exit 1
    ;;
esac
SH

cat > "$work_dir/bin/readlink" <<'SH'
#!/usr/bin/env sh
set -eu

test "${1:-}" = -f
test -n "${2:-}"
printf '%s\n' /usr/lib/dotnet/dotnet
SH

cat > "$work_dir/bin/dpkg-query" <<'SH'
#!/usr/bin/env sh
set -eu

case "$*" in
  '-S /usr/lib/dotnet/dotnet')
    printf '%s\n' 'dotnet-host-10.0: /usr/lib/dotnet/dotnet'
    ;;
  *'${Status}'*'dotnet-sdk-10.0')
    printf '%s\n' 'install ok installed'
    ;;
  *'${Maintainer}'*)
    printf '%s\n' 'Ubuntu Developers <ubuntu-devel-discuss@lists.ubuntu.com>'
    ;;
  *)
    echo "Unexpected dpkg-query fixture call: $*" >&2
    exit 1
    ;;
esac
SH

chmod +x "$work_dir/bin/"*

unstubbed_trace="$work_dir/unstubbed-trace"
unstubbed_state="$work_dir/unstubbed-state"
mkdir -p "$unstubbed_state"

set +e
PATH="$work_dir/bin:$PATH" \
  MININGCORE_HELPER_TEST_BIN="$work_dir/bin" \
  MININGCORE_HELPER_TEST_STATE="$unstubbed_state" \
  MININGCORE_HELPER_TEST_TRACE="$unstubbed_trace" \
  sudo miningcore-unexpected-privileged-command > /dev/null 2>&1
unstubbed_status=$?
set -e

if [[ "$unstubbed_status" -ne 90 ]]; then
  echo "Un-stubbed privileged command did not fail with the fixture boundary status" >&2
  exit 1
fi

if ! grep -Fxq 'unstubbed-privileged miningcore-unexpected-privileged-command' \
    "$unstubbed_trace"; then
  echo "Un-stubbed privileged command did not emit the expected boundary trace" >&2
  cat "$unstubbed_trace" >&2
  exit 1
fi

helpers=(
  build-debian-12.sh
  build-ubuntu-22.04.sh
  build-ubuntu-24.04.sh
  build-ubuntu-26.04.sh
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
  [[ -d "$state" ]] || {
    echo "Source-helper fixture state directory is missing" >&2
    exit 1
  }
  /bin/rm -f -- "$state"/*

  set +e
  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_HELPER_FAIL_TOOL="$fail_tool" \
      MININGCORE_HELPER_FAIL_CALL="$fail_call" \
      MININGCORE_HELPER_TEST_BIN="$work_dir/bin" \
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

  if grep -Fq 'unstubbed-privileged ' "$trace"; then
    echo "$helper reached an un-stubbed privileged command" >&2
    cat "$trace" >&2
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
  cp "$repository_root/scripts/release/assert-warning-free-build.sh" \
    "$sandbox/scripts/release/assert-warning-free-build.sh"
  cp "$repository_root/scripts/release/audit-source-build-warnings.sh" \
    "$sandbox/scripts/release/audit-source-build-warnings.sh"
  cp "$repository_root/scripts/release/verify-ubuntu-dotnet-sdk.sh" \
    "$sandbox/scripts/release/verify-ubuntu-dotnet-sdk.sh"

  bash -n "$sandbox/$helper"

  set +e
  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_HELPER_TEST_BIN="$work_dir/bin" \
      MININGCORE_HELPER_TEST_STATE="$state" \
      MININGCORE_HELPER_TEST_TRACE="$trace" \
      bash "$helper" "$publish_dir"
  ) > "$output" 2>&1
  status=$?
  set -e

  if [[ "$status" -ne 0 ]]; then
    echo "$helper failed during its hermetic success run with status $status" >&2
    cat "$output" >&2
    exit 1
  fi

  if ! grep -Fq "dotnet publish -c Release --framework net10.0 -o $publish_dir" \
      "$trace"; then
    echo "$helper did not reach the expected dotnet publish boundary" >&2
    cat "$trace" >&2
    exit 1
  fi

  if grep -Fq 'unstubbed-privileged ' "$trace"; then
    echo "$helper reached an un-stubbed privileged command" >&2
    cat "$trace" >&2
    exit 1
  fi

  : > "$trace"
  [[ -d "$state" ]] || {
    echo "Source-helper fixture state directory is missing" >&2
    exit 1
  }
  /bin/rm -f -- "$state"/*
  set +e
  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_HELPER_EMIT_WARNING=1 \
      MININGCORE_HELPER_TEST_BIN="$work_dir/bin" \
      MININGCORE_HELPER_TEST_STATE="$state" \
      MININGCORE_HELPER_TEST_TRACE="$trace" \
      bash "$helper" "$publish_dir"
  ) > "$output" 2>&1
  status=$?
  set -e

  if [[ "$status" -eq 0 ]]; then
    echo "$helper accepted an injected compiler warning" >&2
    cat "$output" >&2
    exit 1
  fi

  if ! grep -Fq 'Build emitted compiler or build-system warnings:' "$output"; then
    echo "$helper did not report its warning-audit failure" >&2
    cat "$output" >&2
    exit 1
  fi

  : > "$trace"
  [[ -d "$state" ]] || {
    echo "Source-helper fixture state directory is missing" >&2
    exit 1
  }
  /bin/rm -f -- "$state"/*
  (
    cd "$sandbox"
    PATH="$work_dir/bin:$PATH" \
      MININGCORE_ALLOW_BUILD_WARNINGS=1 \
      MININGCORE_HELPER_EMIT_WARNING=1 \
      MININGCORE_HELPER_TEST_BIN="$work_dir/bin" \
      MININGCORE_HELPER_TEST_STATE="$state" \
      MININGCORE_HELPER_TEST_TRACE="$trace" \
      bash "$helper" "$publish_dir"
  ) > "$output" 2>&1

  if ! grep -Fq 'Do not use this artifact for a release' "$output"; then
    echo "$helper did not identify its warning override as unsuitable for release" >&2
    cat "$output" >&2
    exit 1
  fi

  sudo_count=$(grep -c '^sudo ' "$trace" || true)

  if ((sudo_count == 0)); then
    echo "$helper contains no privileged commands to failure-test"
  else
    for ((fail_call = 1; fail_call <= sudo_count; fail_call++)); do
      assert_fail_closed "$helper" "$sandbox" "$publish_dir" "$trace" "$state" \
        "$output" sudo "$fail_call"
    done
  fi

  if [[ "$helper" == build-debian-12.sh ]]; then
    assert_fail_closed "$helper" "$sandbox" "$publish_dir" "$trace" "$state" \
      "$output" wget 1
    assert_fail_closed "$helper" "$sandbox" "$publish_dir" "$trace" "$state" \
      "$output" rm 1
  fi
done

echo "Source-build helpers passed hermetic success and fail-closed checks"
