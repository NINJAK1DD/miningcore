#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
audit="$repository_root/scripts/release/assert-warning-free-build.sh"
source_audit="$repository_root/scripts/release/audit-source-build-warnings.sh"
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

clean_log="$work_dir/clean.log"
printf '%s\n' \
  'Build succeeded.' \
  '    0 Warning(s)' \
  '    0 Error(s)' > "$clean_log"
bash "$audit" "$clean_log" > "$work_dir/clean-output"

warning_cases=(
  'fixture.c:1:1: warning: compiler diagnostic'
  'fixture.c:1: warning: compiler diagnostic without column'
  'cc1plus: warning: command-line diagnostic'
  '/usr/bin/ld: warning: missing .note.GNU-stack section implies executable stack'
  '/usr/bin/ld.bfd: warning: foo.o has a LOAD segment with RWX permissions'
  '/opt/toolchain/bin/ld.gold: warning: libfoo.so, needed by x, not found'
  'ld.lld: warning: foo.o: requires executable stack'
  'collect2: warning: ld returned a diagnostic'
  '/usr/bin/ar: warning: creating libfixture.a'
  'as: warning: end of file not at end of a line'
  'ranlib: warning: libfixture.a has no symbols'
  'Fixture.cs(1,1): warning CS8981: managed diagnostic'
  'fixture.c(1,1): warning G1234ABCD: native MSBuild diagnostic'
  'CMake Warning at CMakeLists.txt:1 (message):'
  'CMake Deprecation Warning at CMakeLists.txt:1 (message):'
  'CMake Warning (dev) at CMakeLists.txt:1 (message):'
  '  CMake Developer Warning at CMakeLists.txt:1 (message):'
)

for warning_text in "${warning_cases[@]}"; do
  printf '%s\n' "$warning_text" > "$work_dir/warning.log"

  set +e
  bash "$audit" "$work_dir/warning.log" > "$work_dir/warning-output" 2>&1
  status=$?
  set -e

  if [[ "$status" -ne 1 ]]; then
    echo "Warning audit accepted a warning with status $status: $warning_text" >&2
    cat "$work_dir/warning-output" >&2
    exit 1
  fi

  if ! grep -Fq "$warning_text" "$work_dir/warning-output"; then
    echo "Warning audit did not identify the rejected warning: $warning_text" >&2
    cat "$work_dir/warning-output" >&2
    exit 1
  fi
done

ignored_cases=(
  "warning: redirecting to https://github.com/example/project.git/"
  "warning: unable to access '/root/.gitconfig'"
  'Fixture.cs(1,1): Warnung CS8981: localized managed diagnostic'
  'Documentation warning: this is not compiler output'
)

for ignored_text in "${ignored_cases[@]}"; do
  printf '%s\n' "$ignored_text" > "$work_dir/ignored.log"
  bash "$audit" "$work_dir/ignored.log" > "$work_dir/ignored-output"
done

printf '%s\n' 'fixture.c:1:1: warning: override fixture' > "$work_dir/override.log"
MININGCORE_ALLOW_BUILD_WARNINGS=1 \
  bash "$source_audit" "$work_dir/override.log" > "$work_dir/override-output" 2>&1

if ! grep -Fq 'Do not use this artifact for a release' "$work_dir/override-output"; then
  echo "Source-build warning override did not emit its release-safety warning" >&2
  cat "$work_dir/override-output" >&2
  exit 1
fi

set +e
MININGCORE_ALLOW_BUILD_WARNINGS=1 \
  bash "$source_audit" "$work_dir/missing.log" > "$work_dir/override-missing-output" 2>&1
override_missing_status=$?
MININGCORE_ALLOW_BUILD_WARNINGS=yes \
  bash "$source_audit" "$work_dir/clean.log" > "$work_dir/invalid-override-output" 2>&1
invalid_override_status=$?
set -e

if [[ "$override_missing_status" -ne 70 ]]; then
  echo "Source-build warning override bypassed an unreadable-log failure" >&2
  cat "$work_dir/override-missing-output" >&2
  exit 1
fi

if [[ "$invalid_override_status" -ne 64 ]]; then
  echo "Source-build warning policy accepted an invalid override value" >&2
  cat "$work_dir/invalid-override-output" >&2
  exit 1
fi

set +e
bash "$audit" "$work_dir/missing.log" > "$work_dir/missing-output" 2>&1
status=$?
set -e

if [[ "$status" -ne 70 ]]; then
  echo "Warning audit returned $status instead of 70 for an unreadable log" >&2
  cat "$work_dir/missing-output" >&2
  exit 1
fi

if grep -Fq "$work_dir" "$work_dir/missing-output"; then
  echo "Warning audit exposed its private log path" >&2
  cat "$work_dir/missing-output" >&2
  exit 1
fi

mkdir "$work_dir/log-directory"

set +e
bash "$audit" "$work_dir/log-directory" > "$work_dir/directory-output" 2>&1
directory_status=$?
set -e

if [[ "$directory_status" -ne 70 ]]; then
  echo "Warning audit returned $directory_status instead of 70 for a directory" >&2
  cat "$work_dir/directory-output" >&2
  exit 1
fi

if grep -Fq "$work_dir" "$work_dir/directory-output"; then
  echo "Warning audit exposed its private log path for a non-regular input" >&2
  cat "$work_dir/directory-output" >&2
  exit 1
fi

mkdir "$work_dir/bin"
cat > "$work_dir/bin/grep" <<'EOF'
#!/usr/bin/env bash
exit 2
EOF
chmod +x "$work_dir/bin/grep"

set +e
PATH="$work_dir/bin:$PATH" \
  bash "$audit" "$clean_log" > "$work_dir/grep-error-output" 2>&1
grep_error_status=$?
set -e

if [[ "$grep_error_status" -ne 70 ]]; then
  echo "Warning audit returned $grep_error_status instead of 70 for a grep failure" >&2
  cat "$work_dir/grep-error-output" >&2
  exit 1
fi

if grep -Fq "$work_dir" "$work_dir/grep-error-output"; then
  echo "Warning audit exposed its private log path after a grep failure" >&2
  cat "$work_dir/grep-error-output" >&2
  exit 1
fi

echo "Build warning audit contract passed"
