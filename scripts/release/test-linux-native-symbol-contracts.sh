#!/usr/bin/env bash

set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repository_root=$(cd "$script_dir/../.." && pwd)
validator="$script_dir/assert-linux-native-symbol-contracts.py"
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

while IFS= read -r library; do
  library=${library%$'\r'}
  makefile="$repository_root/src/Native/${library%.so}/Makefile"
  if [[ ! -f "$makefile" ]] || ! grep -Fq -- '-Wl,--no-undefined' "$makefile"; then
    echo "$library does not enforce the Linux no-undefined link contract" >&2
    exit 1
  fi
done < "$script_dir/linux-native-libraries.txt"

fixture="$work_dir/fixture"
publish_dir="$fixture/publish"
source_dir="$fixture/source"
inventory="$fixture/inventory.txt"
exceptions="$fixture/exceptions.json"
fake_ldd="$fixture/fake-ldd"
fake_nm="$fixture/fake-nm"

reset_fixture() {
  rm -rf -- "$fixture"
  mkdir -p "$publish_dir" "$source_dir"

  printf '%s\n' libalpha.so libbeta.so > "$inventory"
  : > "$publish_dir/libalpha.so"
  : > "$publish_dir/libbeta.so"

  cat > "$source_dir/Alpha.cs" <<'CS'
using System.Runtime.InteropServices;

internal static class Alpha
{
    [DllImport(
        "libalpha",
        EntryPoint   =   "alpha_export",
        CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Invoke();
}
CS

  cat > "$source_dir/Beta.cs" <<'CS'
using System.Runtime.InteropServices;

internal static partial class Beta
{
    [LibraryImportAttribute ( "libbeta" )]
    internal static partial void beta_export();
}
CS

  cat > "$fake_ldd" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
library=${!#}

case "${TEST_LDD_MODE:-clean}" in
  clean)
    echo 'libc.so.6 => /lib/libc.so.6 (0x1)'
    ;;
  optional)
    if [[ "$library" == */libalpha.so ]]; then
      echo "undefined symbol: optional_symbol ($library)"
    fi
    ;;
  missing-provider)
    echo 'libmissing.so => not found'
    ;;
  malformed)
    echo 'undefined symbol: malformed without object'
    ;;
  hostile-failure)
    printf '::error title=hostile::payload\r##[error]legacy\n' >&2
    exit 9
    ;;
  failure)
    exit 9
    ;;
  *)
    echo "Unknown TEST_LDD_MODE" >&2
    exit 98
    ;;
esac
SH

  cat > "$fake_nm" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
library=${!#}

if [[ "${TEST_NM_MODE:-clean}" == failure ]]; then
  exit 8
fi

case "$(basename "$library")" in
  libalpha.so)
    if [[ "${TEST_NM_MODE:-clean}" != missing ]]; then
      echo 'alpha_export T 0 0'
    fi
    ;;
  libbeta.so)
    echo 'beta_export T 0 0'
    ;;
  libgamma.so)
    echo 'gamma_export T 0 0'
    ;;
  *)
    exit 7
    ;;
esac
SH

  chmod 0700 "$fake_ldd" "$fake_nm"
}

run_validator() {
  python3 "$validator" \
    "$publish_dir" \
    "$source_dir" \
    "$inventory" \
    --ldd "$fake_ldd" \
    --nm "$fake_nm" \
    "$@"
}

expect_structural_failure() {
  local description=$1
  shift
  local output
  local status

  set +e
  output=$("$@" 2>&1)
  status=$?
  set -e

  if [[ "$status" -ne 70 ]]; then
    echo "$description returned $status instead of structural status 70" >&2
    printf '%s\n' "$output" >&2
    exit 1
  fi
}

run_with_missing_export() {
  TEST_NM_MODE=missing run_validator
}

run_with_ldd_failure() {
  TEST_LDD_MODE=failure run_validator
}

run_with_nm_failure() {
  TEST_NM_MODE=failure run_validator
}

run_with_missing_provider() {
  TEST_LDD_MODE=missing-provider run_validator
}

run_with_malformed_relocation() {
  TEST_LDD_MODE=malformed run_validator
}

reset_fixture
run_validator

reset_fixture
run_with_unapproved_symbol() {
  TEST_LDD_MODE=optional run_validator "$@"
}
expect_structural_failure 'unapproved unresolved symbol' run_with_unapproved_symbol

reset_fixture
cat > "$exceptions" <<'JSON'
[
  {
    "library": "libalpha.so",
    "symbol": "optional_symbol",
    "consumer": "Hermetic optional-path fixture",
    "rationale": "The fixture supplies this symbol only when that path is loaded."
  }
]
JSON
TEST_LDD_MODE=optional run_validator --exceptions "$exceptions"

reset_fixture
cat > "$exceptions" <<'JSON'
[
  {
    "library": "libbeta.so",
    "symbol": "optional_symbol",
    "consumer": "Wrong-library fixture",
    "rationale": "An exception must never authorize the same symbol in another library."
  }
]
JSON
expect_structural_failure 'wrong-library exception' \
  run_with_unapproved_symbol --exceptions "$exceptions"

reset_fixture
cat > "$exceptions" <<'JSON'
[
  {
    "library": "libalpha.so",
    "symbol": "optional_symbol",
    "consumer": "Hermetic optional-path fixture",
    "rationale": "This entry must be rejected after the unresolved import disappears."
  }
]
JSON
expect_structural_failure 'stale exception' run_validator --exceptions "$exceptions"

reset_fixture
expect_structural_failure 'missing managed export' run_with_missing_export

reset_fixture
expect_structural_failure 'dynamic-loader inspection failure' run_with_ldd_failure

reset_fixture
expect_structural_failure 'export inspection failure' run_with_nm_failure

reset_fixture
expect_structural_failure 'missing provider' run_with_missing_provider

reset_fixture
expect_structural_failure 'malformed relocation diagnostic' run_with_malformed_relocation

reset_fixture
rm "$publish_dir/libalpha.so"
expect_structural_failure 'missing native artifact' run_validator

reset_fixture
rm "$publish_dir/libalpha.so"
mkdir "$publish_dir/libalpha.so"
expect_structural_failure 'non-regular native artifact' run_validator

reset_fixture
rm -rf "$source_dir"
expect_structural_failure 'missing wrapper directory' run_validator

reset_fixture
mkfifo "$source_dir/Unreadable.cs"
expect_structural_failure 'non-regular wrapper source' run_validator

reset_fixture
cat >> "$source_dir/Alpha.cs" <<'CS'

internal static class Ambiguous
{
    [DllImport("libbeta", EntryPoint = "beta_export")]
    internal static extern void Invoke();
}
CS
expect_structural_failure 'ambiguous wrapper mapping' run_validator

reset_fixture
cat > "$source_dir/Alias.cs" <<'CS'
using NativeImport = System.Runtime.InteropServices.DllImportAttribute;

internal static class AliasedImport
{
    [NativeImport("libalpha", EntryPoint = "hidden_export")]
    internal static extern void Invoke();
}
CS
expect_structural_failure 'aliased import attribute' run_validator

reset_fixture
cat >> "$inventory" <<'EOF'
libgamma.so
EOF
: > "$publish_dir/libgamma.so"
cat > "$source_dir/Gamma.cs" <<'CS'
using System.Runtime.InteropServices;

internal static class Gamma
{
    [DllImport("libgamma", EntryPoint = "gamma_export")]
    internal static extern void Invoke();
}
CS
run_validator

reset_fixture
printf 'libalpha.so\rlibbeta.so\n' > "$inventory"
expect_structural_failure 'invalid carriage return in inventory' run_validator

reset_fixture
hostile_output="$work_dir/hostile-output"
set +e
TEST_LDD_MODE=hostile-failure run_validator > "$hostile_output" 2>&1
hostile_status=$?
set -e

if [[ "$hostile_status" -ne 70 ]]; then
  echo "Hostile tool failure returned $hostile_status instead of 70" >&2
  exit 1
fi

if grep -Eq '^[[:space:]]*(::|##\[)' "$hostile_output"; then
  echo 'A tool-controlled workflow command was repeated as an executable diagnostic' >&2
  exit 1
fi

echo 'Linux native symbol contract tests passed'
