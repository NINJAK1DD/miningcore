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

if [[ "${TEST_ASSERT_ENV:-0}" == 1 ]]; then
  if [[ -n "${LD_PRELOAD:-}" || -n "${LD_LIBRARY_PATH:-}" ]]; then
    echo 'The ldd environment was not isolated' >&2
    exit 97
  fi
fi

case "${TEST_LDD_MODE:-clean}" in
  clean)
    echo 'libc.so.6 => /lib/libc.so.6 (0x1)'
    ;;
  optional)
    if [[ "$library" == */libalpha.so ]]; then
      echo "undefined symbol: optional_symbol ($library)"
    fi
    ;;
  optional-parentheses)
    if [[ "$library" == */libalpha.so ]]; then
      echo "undefined symbol: optional_symbol (${library%/*}/path (copy)/libalpha.so)"
    fi
    ;;
  optional-versioned)
    if [[ "$library" == */libalpha.so ]]; then
      echo "undefined symbol: optional_symbol@PROJECT_1.0 ($library)"
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

if [[ "${TEST_ASSERT_ENV:-0}" == 1 ]] &&
  [[ -n "${LD_PRELOAD:-}" || -n "${LD_LIBRARY_PATH:-}" ]]; then
  echo 'The nm environment was not isolated' >&2
  exit 97
fi

if [[ "${TEST_NM_MODE:-clean}" == failure ]]; then
  exit 8
fi

if [[ " $* " == *' --undefined-only '* ]]; then
  case "${TEST_NM_MODE:-clean}:$(basename "$library")" in
    weak-project:libalpha.so)
      echo 'weak_missing w'
      ;;
    weak-project-versioned:libalpha.so)
      echo 'weak_missing@PROJECT_1.0 w'
      ;;
    weak-toolchain:libalpha.so)
      echo '_ITM_registerTMCloneTable w'
      echo '__cxa_finalize@GLIBC_2.17 w'
      echo '__cxa_pure_virtual@CXXABI_1.4 w'
      ;;
  esac
  exit 0
fi

case "$(basename "$library")" in
  libalpha.so)
    case "${TEST_NM_MODE:-clean}" in
      missing)
        ;;
      data)
        echo 'alpha_export D 0 8'
        ;;
      bss)
        echo 'alpha_export B 0 8'
        ;;
      read-only)
        echo 'alpha_export R 0 8'
        ;;
      local-text)
        echo 'alpha_export t 0 8'
        ;;
      undefined)
        echo 'alpha_export U'
        ;;
      missing-all)
        ;;
      *)
        echo 'alpha_export T 0 0'
        ;;
    esac
    ;;
  libbeta.so)
    if [[ "${TEST_NM_MODE:-clean}" != missing-all ]]; then
      echo 'beta_export T 0 0'
    fi
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
    --managed-project-directory "$fixture" \
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

expect_contract_failure() {
  local description=$1
  shift
  local output
  local status

  set +e
  output=$("$@" 2>&1)
  status=$?
  set -e

  if [[ "$status" -ne 1 ]]; then
    echo "$description returned $status instead of contract status 1" >&2
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

run_with_non_callable_export() {
  TEST_NM_MODE=$1 run_validator
}

run_with_weak_project_import() {
  TEST_NM_MODE=weak-project run_validator
}

run_with_versioned_weak_project_import() {
  TEST_NM_MODE=weak-project-versioned run_validator "$@"
}

reset_fixture
run_validator

reset_fixture
cat > "$source_dir/Alpha.cs" <<'CS'
internal static class Alpha
{
    [global::System.Runtime.InteropServices.DllImport("libalpha.so",
        EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}
CS
cat > "$source_dir/Beta.cs" <<'CS'
using System;
using RI = System.Runtime.InteropServices;

internal static partial class Beta
{
    [method: Obsolete, RI.LibraryImport("libbeta.so")]
    internal static partial void beta_export();
}
CS
run_validator

reset_fixture
TEST_ASSERT_ENV=1 LD_LIBRARY_PATH=/host/injected run_validator

reset_fixture
printf 'libalpha.so\r\nlibbeta.so\r\n' > "$inventory"
run_validator

reset_fixture
run_with_unapproved_symbol() {
  TEST_LDD_MODE=optional run_validator "$@"
}
expect_contract_failure 'unapproved unresolved symbol' run_with_unapproved_symbol

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
    "library": "libalpha.so",
    "symbol": "optional_symbol",
    "consumer": "Parenthesized-path fixture",
    "rationale": "The object path syntax must not alter exception matching."
  }
]
JSON
TEST_LDD_MODE=optional-parentheses run_validator --exceptions "$exceptions"

reset_fixture
cat > "$exceptions" <<'JSON'
[
  {
    "library": "libalpha.so",
    "symbol": "optional_symbol",
    "consumer": "Version-normalization fixture",
    "rationale": "Relocation and weak-symbol paths share one unversioned identity."
  }
]
JSON
TEST_LDD_MODE=optional-versioned run_validator --exceptions "$exceptions"

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
expect_contract_failure 'wrong-library exception' \
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
expect_contract_failure 'stale exception' run_validator --exceptions "$exceptions"

reset_fixture
expect_contract_failure 'missing managed export' run_with_missing_export

for symbol_type in data bss read-only local-text undefined; do
  reset_fixture
  expect_contract_failure "non-callable $symbol_type export" \
    run_with_non_callable_export "$symbol_type"
done

reset_fixture
set +e
aggregate_output=$(TEST_NM_MODE=missing-all run_validator 2>&1)
aggregate_status=$?
set -e
if [[ "$aggregate_status" -ne 1 ]] ||
  ! grep -Fq 'libalpha.so does not export managed entry point: alpha_export' \
    <<< "$aggregate_output" ||
  ! grep -Fq 'libbeta.so does not export managed entry point: beta_export' \
    <<< "$aggregate_output"; then
  echo 'Contract violations were not aggregated across libraries' >&2
  printf '%s\n' "$aggregate_output" >&2
  exit 1
fi

reset_fixture
expect_contract_failure 'unapproved weak project import' run_with_weak_project_import

reset_fixture
expect_contract_failure 'unapproved versioned weak project import' \
  run_with_versioned_weak_project_import

reset_fixture
cat > "$exceptions" <<'JSON'
[
  {
    "library": "libalpha.so",
    "symbol": "weak_missing",
    "consumer": "Version-normalization fixture",
    "rationale": "ELF symbol versions must not change exception identity."
  }
]
JSON
run_with_versioned_weak_project_import --exceptions "$exceptions"

reset_fixture
cat > "$exceptions" <<'JSON'
[
  {
    "library": "libalpha.so",
    "symbol": "weak_missing@PROJECT_1.0",
    "consumer": "Versioned-manifest fixture",
    "rationale": "Manifest symbols must use the canonical unversioned identity."
  }
]
JSON
expect_structural_failure 'versioned exception-manifest symbol' \
  run_with_versioned_weak_project_import --exceptions "$exceptions"

reset_fixture
TEST_NM_MODE=weak-toolchain run_validator

reset_fixture
expect_structural_failure 'dynamic-loader inspection failure' run_with_ldd_failure

reset_fixture
expect_structural_failure 'export inspection failure' run_with_nm_failure

reset_fixture
expect_contract_failure 'missing provider' run_with_missing_provider

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

for expression in 'Constants.Alpha' 'nameof(alpha_export)' '"alpha_" + "export"'; do
  reset_fixture
  cat > "$source_dir/Alpha.cs" <<CS
using System.Runtime.InteropServices;

internal static class Alpha
{
    private const string AlphaName = "alpha_export";

    [DllImport("libalpha", EntryPoint = $expression)]
    internal static extern void alpha_export();
}

internal static class Constants
{
    internal const string Alpha = "alpha_export";
}
CS
  expect_structural_failure "non-literal EntryPoint expression $expression" run_validator
done

reset_fixture
cat >> "$source_dir/Alpha.cs" <<'CS'

internal static class LiteralNoise
{
    private const string Ordinary = "DllImport and [DllImport(\"libevil\")]";
    private const string Verbatim = @"DllImport and [DllImport(""libevil"")]";
    private const string Interpolated = $"DllImport {1}";
    private const string InterpolatedVerbatim = @$"DllImport {1}";
    private const string Raw = """DllImport and [DllImport("libevil")]""";
    private const string InterpolatedRaw = $$"""DllImport {{1}}""";
}
CS
run_validator

reset_fixture
cat > "$source_dir/Conditional.cs" <<'CS'
using System.Runtime.InteropServices;

#if WINDOWS
internal static class Conditional
{
    [DllImport("libalpha", EntryPoint = "conditional_export")]
    internal static extern void Invoke();
}
#endif
CS
expect_structural_failure 'conditional native import' run_validator

reset_fixture
cat > "$fixture/Outside.cs" <<'CS'
using System.Runtime.InteropServices;

internal static class Outside
{
    [DllImport("libalpha", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}
CS
expect_structural_failure 'packaged import outside reviewed directory' run_validator

expect_outside_import_rejected() {
  local description=$1
  local source=$2
  local output
  local status

  reset_fixture
  printf '%s\n' "$source" > "$fixture/OutsideVariant.cs"

  set +e
  output=$(run_validator 2>&1)
  status=$?
  set -e

  if [[ "$status" -ne 70 ]] ||
    [[ "$output" != *'Packaged native import is outside the reviewed Native directory:'* ]]; then
    echo "$description did not fail at the outer packaged-import boundary" >&2
    printf '%s\n' "$output" >&2
    exit 1
  fi
}

expect_outside_import_rejected 'global-qualified packaged import' \
  'internal static class Outside
{
    [global::System.Runtime.InteropServices.DllImport("libalpha",
        EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}'

expect_outside_import_rejected 'alias-qualified packaged import' \
  'using RI = System.Runtime.InteropServices;

internal static class Outside
{
    [RI.DllImport("libalpha", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}'

expect_outside_import_rejected 'filename-form packaged import' \
  'using System.Runtime.InteropServices;

internal static class Outside
{
    [DllImport("libalpha.so", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}'

expect_outside_import_rejected 'escaped-literal packaged import' \
  'using System.Runtime.InteropServices;

internal static class Outside
{
    [DllImport("lib\u0061lpha", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}'

expect_outside_import_rejected 'qualified LibraryImport packaged import' \
  'internal static partial class Outside
{
    [global::System.Runtime.InteropServices.LibraryImport("libalpha.so",
        EntryPoint = "alpha_export")]
    internal static partial void Invoke();
}'

expect_outside_import_rejected 'mid-line packaged import' \
  'using System.Runtime.InteropServices;
internal static class Outside { [DllImport("libalpha",
    EntryPoint = "alpha_export")] internal static extern void Invoke(); }'

expect_outside_import_rejected 'multi-attribute packaged import' \
  'using System;
using System.Runtime.InteropServices;

internal static class Outside
{
    [Obsolete, DllImport("libalpha", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}'

expect_outside_import_rejected 'targeted packaged import' \
  'using System.Runtime.InteropServices;

internal static class Outside
{
    [method: DllImport("libalpha", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}'

expect_outside_import_rejected 'comment-separated packaged import' \
  'using System.Runtime.InteropServices;

internal static class Outside
{
    [DllImport(/* reviewed literal */ "libalpha", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}'

reset_fixture
mkdir -p "$fixture/obj/Release/net10.0/generated" "$fixture/bin/generated"
cat > "$fixture/obj/Release/net10.0/generated/LibraryImports.g.cs" <<'CS'
// <auto-generated/>
using System.Runtime.InteropServices;

internal static class GeneratedInterop
{
    [DllImport("libalpha", EntryPoint = "alpha_export")]
    internal static extern void Invoke();
}
CS
cp "$fixture/obj/Release/net10.0/generated/LibraryImports.g.cs" \
  "$fixture/bin/generated/LibraryImports.g.cs"
run_validator

reset_fixture
cat > "$fixture/OutsideSystemImport.cs" <<'CS'
using System.Runtime.InteropServices;

internal static class NativeLibraries
{
    internal const string Libc = "libc";
}

internal static class OutsideSystemImport
{
    [DllImport(NativeLibraries.Libc, EntryPoint = "getpid")]
    internal static extern int GetProcessId();
}
CS
run_validator

reset_fixture
cat > "$fixture/OutsideDocumentation.cs" <<'CS'
internal static class OutsideDocumentation
{
    private const string Example = "[DllImport(\"libalpha\")]";

    internal static string Format(string value) => $"prefix {value[0]}";
}
CS
run_validator

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
