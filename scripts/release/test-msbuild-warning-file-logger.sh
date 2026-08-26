#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
work_dir=$(mktemp -d)

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

warning_message='file logger integration warning'
warning="fixture.c:1:1: warning: $warning_message"
emitter="$work_dir/emit-warning.sh"
project="$work_dir/FileLogger.proj"
build_log="$work_dir/build.log"
audit_output="$work_dir/audit-output"

cat > "$emitter" <<EOF
#!/usr/bin/env sh
printf '%s\n' '$warning'
EOF

cat > "$project" <<'EOF'
<Project DefaultTargets="Build">
  <Target Name="Build">
    <Exec Command="$(WarningEmitter)" />
  </Target>
</Project>
EOF

chmod +x "$emitter"
: > "$build_log"

set +e
bash "$repository_root/scripts/release/run-warning-audited-dotnet.sh" \
  "$build_log" build "$project" -nologo -verbosity:minimal \
  "-p:WarningEmitter=$emitter" > "$audit_output" 2>&1
audit_status=$?
set -e

if [[ ! -s "$build_log" ]] || ! grep -Fq "$warning_message" "$build_log"; then
  echo "MSBuild's normal-verbosity file logger omitted external tool output" >&2
  cat "$audit_output" >&2
  exit 1
fi

if [[ "$audit_status" -ne 1 ]] || ! grep -Fq "$warning_message" "$audit_output"; then
  echo "Warning-audited dotnet wrapper did not reject the file-logger diagnostic" >&2
  cat "$audit_output" >&2
  exit 1
fi

echo "MSBuild file logger preserved external warning diagnostics"
