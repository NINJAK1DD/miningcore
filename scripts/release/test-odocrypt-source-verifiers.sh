#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
bash_verifier="$repository_root/scripts/release/verify-pinned-source-files.sh"
powershell_verifier="$repository_root/scripts/release/build-windows-odocrypt.ps1"
work_dir=$(mktemp -d)
fixture_root="$work_dir/Native"
manifest="$fixture_root/libodocrypt/upstream.sha256"
source_file="$fixture_root/libmultihash/test-source.cpp"

cleanup()
{
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

mkdir -p "$fixture_root/libodocrypt" "$fixture_root/libmultihash"

if command -v cygpath >/dev/null 2>&1; then
  windows_path_converter=cygpath
elif command -v wslpath >/dev/null 2>&1; then
  windows_path_converter=wslpath
else
  echo "A Windows path converter is required for verifier parity tests" >&2
  exit 70
fi

if ! command -v powershell.exe >/dev/null 2>&1; then
  echo "Windows PowerShell is required for verifier parity tests" >&2
  exit 70
fi

to_windows_path()
{
  "$windows_path_converter" -w "$1"
}

run_powershell_verifier()
{
  powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass \
    -File "$(to_windows_path "$powershell_verifier")" \
    -SourceRoot "$(to_windows_path "$fixture_root")" -VerifyOnly
}

write_canonical_fixture()
{
  printf 'alpha\nbeta' > "$source_file"
  printf '%s  libmultihash/test-source.cpp\n' \
    "$(sha256sum -- "$source_file" | awk '{print $1}')" > "$manifest"
}

require_both_accept()
{
  local description=$1

  bash "$bash_verifier" "$fixture_root" "$manifest" >/dev/null
  run_powershell_verifier >/dev/null
  printf 'Both source verifiers accepted %s\n' "$description"
}

require_both_reject()
{
  local description=$1
  local bash_status powershell_status

  set +e
  bash "$bash_verifier" "$fixture_root" "$manifest" >/dev/null 2>&1
  bash_status=$?
  run_powershell_verifier >/dev/null 2>&1
  powershell_status=$?
  set -e

  if [[ "$bash_status" -eq 0 || "$powershell_status" -eq 0 ]]; then
    printf 'Verifier parity failure for %s: Bash=%d PowerShell=%d\n' \
      "$description" "$bash_status" "$powershell_status" >&2
    exit 1
  fi

  printf 'Both source verifiers rejected %s\n' "$description"
}

write_canonical_fixture
require_both_accept "canonical LF input"

printf 'alpha\r\nbeta\r' > "$source_file"
require_both_accept "CRLF input with a final lone carriage return"

write_canonical_fixture
printf '\ninjected source drift\n' >> "$source_file"
require_both_reject "substantive source drift"

write_canonical_fixture
printf '%064d  ../outside.cpp\n' 0 > "$manifest"
require_both_reject "an unsafe manifest path"

printf 'malformed manifest entry\n' > "$manifest"
require_both_reject "a malformed manifest"

echo "Odocrypt Bash/PowerShell source-verifier parity tests passed"
