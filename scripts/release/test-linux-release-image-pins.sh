#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
monitor="$repository_root/scripts/release/run-linux-release-image-pin-monitor.sh"
workflow="$repository_root/.github/workflows/release-image-pins.yml"
release_docs="$repository_root/docs/releases.md"
source "$repository_root/scripts/release/linux-release-targets.sh"
work_dir=$(mktemp -d)
fake_bin="$work_dir/bin"
mkdir -p "$fake_bin"

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

cat > "$fake_bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -eq 2 && "$1" = buildx && "$2" = version ]]; then
  if [[ ${MININGCORE_TEST_BUILDX_FAILURE:-} = 1 ]]; then
    echo "docker: 'buildx' is not a docker command" >&2
    exit 1
  fi

  echo 'github.com/docker/buildx v0.0.0-test'
  exit 0
fi

if [[ "$#" -eq 4 && "$1" = buildx && "$2" = imagetools &&
    "$3" = inspect && "$4" = --help ]]; then
  if [[ ${MININGCORE_TEST_IMAGETOOLS_FAILURE:-} = 1 ]]; then
    echo "unknown command 'imagetools' for 'docker buildx'" >&2
    exit 1
  fi

  echo 'Usage: docker buildx imagetools inspect NAME'
  exit 0
fi

if [[ "$#" -ne 4 || "$1" != buildx || "$2" != imagetools || "$3" != inspect ]]; then
  echo "Unexpected docker invocation: $*" >&2
  exit 64
fi

if [[ ${MININGCORE_TEST_INSPECT_FAILURE:-} = 1 ||
    ${MININGCORE_TEST_TRANSIENT_FAILURE_TAG:-} = "$4" ]]; then
  echo 'injected registry timeout' >&2
  exit 1
fi

if [[ ${MININGCORE_TEST_WORKFLOW_COMMAND_TAG:-} = "$4" ]]; then
  echo '::error title=Injected by registry::pwned' >&2
  echo 'response: connection reset by peer' >&2
  exit 1
fi

if [[ ${MININGCORE_TEST_NOT_FOUND_TAG:-} = "$4" ]]; then
  printf 'ERROR: docker.io/library/%s: not found\n' "$4" >&2
  exit 1
fi

if [[ ${MININGCORE_TEST_INTERNAL_SERVER_ERROR_TAG:-} = "$4" ]]; then
  printf 'ERROR: docker.io/library/%s: 500 Internal Server Error\n' "$4" >&2
  exit 1
fi

if [[ ${MININGCORE_TEST_UNAUTHORIZED_TAG:-} = "$4" ]]; then
  printf 'ERROR: docker.io/library/%s: unauthorized: authentication required\n' "$4" >&2
  exit 1
fi

if [[ ${MININGCORE_TEST_MALFORMED_INSPECTION:-} = 1 ]]; then
  printf 'Name:      docker.io/library/%s\n' "$4"
  printf 'MediaType: application/vnd.oci.image.index.v1+json\n'
  exit 0
fi

case "$4" in
  ubuntu:26.04)
    digest=$MININGCORE_TEST_RESOLUTE_DIGEST
    ;;
  ubuntu:22.04)
    digest=$MININGCORE_TEST_JAMMY_DIGEST
    ;;
  *)
    echo "Unexpected image tag: $4" >&2
    exit 64
    ;;
esac

printf 'Name:      docker.io/library/%s\n' "$4"
printf 'MediaType: application/vnd.oci.image.index.v1+json\n'
printf 'Digest:    %s\n' "$digest"
EOF
chmod 0755 "$fake_bin/docker"

export MININGCORE_TEST_RESOLUTE_DIGEST
export MININGCORE_TEST_JAMMY_DIGEST
MININGCORE_TEST_RESOLUTE_DIGEST=$(miningcore_linux_release_target_image_digest 26.04)
MININGCORE_TEST_JAMMY_DIGEST=$(miningcore_linux_release_target_image_digest 22.04)

PATH="$fake_bin:$PATH" bash "$checker"

stale_digest=sha256:1111111111111111111111111111111111111111111111111111111111111111
set +e
failure_output=$(
  MININGCORE_TEST_JAMMY_DIGEST=$stale_digest \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
failure_status=$?
set -e

if [[ "$failure_status" -eq 0 ]]; then
  echo 'Image-pin freshness check accepted a moved Ubuntu tag' >&2
  exit 1
fi

if ! grep -Fq 'ubuntu:22.04 now resolves to sha256:111111' <<<"$failure_output" ||
    ! grep -Fq 'Review upstream changes' <<<"$failure_output"; then
  echo 'Image-pin freshness check did not explain the required review' >&2
  printf '%s\n' "$failure_output" >&2
  exit 1
fi

set +e
registry_output=$(
  MININGCORE_TEST_INSPECT_FAILURE=1 PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
registry_status=$?
set -e

if [[ "$registry_status" -ne 69 ]] ||
    ! grep -Fq 'Transient registry failure while resolving ubuntu:26.04' \
      <<<"$registry_output" ||
    ! grep -Fq 'injected registry timeout' <<<"$registry_output" ||
    ! grep -Fq \
      'Transient image checks unresolved: ubuntu:26.04, ubuntu:22.04' \
      <<<"$registry_output"; then
  echo 'Image-pin freshness check did not distinguish registry failure from drift' >&2
  printf '%s\n' "$registry_output" >&2
  exit 1
fi

set +e
workflow_command_output=$(
  GITHUB_ACTIONS=true \
    MININGCORE_TEST_WORKFLOW_COMMAND_TAG=ubuntu:26.04 \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
workflow_command_status=$?
set -e

workflow_command='::error title=Injected by registry::pwned'
workflow_guard_token=$(
  sed -n 's/^::stop-commands::\([0-9a-f]\{32\}\)$/\1/p' \
    <<<"$workflow_command_output" | head -n 1
)

if [[ "$workflow_command_status" -ne 69 ]] ||
    [[ ! "$workflow_guard_token" =~ ^[0-9a-f]{32}$ ]]; then
  echo 'Image-pin checker did not guard registry output in GitHub Actions' >&2
  printf '%s\n' "$workflow_command_output" >&2
  exit 1
fi

workflow_stop_line=$(awk -v needle="::stop-commands::$workflow_guard_token" \
  'index($0, needle) { print NR; exit }' <<<"$workflow_command_output")
workflow_payload_line=$(awk -v needle="$workflow_command" \
  'index($0, needle) { print NR; exit }' <<<"$workflow_command_output")
workflow_resume_line=$(awk -v needle="::$workflow_guard_token::" \
  'index($0, needle) { print NR; exit }' <<<"$workflow_command_output")

if [[ -z "$workflow_stop_line" || -z "$workflow_payload_line" ||
    -z "$workflow_resume_line" ]] ||
    (( workflow_stop_line >= workflow_payload_line ||
      workflow_payload_line >= workflow_resume_line )); then
  echo 'Image-pin checker emitted registry output outside its workflow-command guard' >&2
  printf '%s\n' "$workflow_command_output" >&2
  exit 1
fi

guard_failure_bin="$work_dir/guard-failure-bin"
mkdir -p "$guard_failure_bin"

cat > "$guard_failure_bin/od" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
chmod 0755 "$guard_failure_bin/od"

set +e
guard_failure_output=$(
  GITHUB_ACTIONS=true \
    MININGCORE_TEST_WORKFLOW_COMMAND_TAG=ubuntu:26.04 \
    PATH="$guard_failure_bin:$fake_bin:$PATH" bash "$checker" 2>&1
)
guard_failure_status=$?
set -e

if [[ "$guard_failure_status" -ne 69 ]] ||
    grep -Fq "$workflow_command" <<<"$guard_failure_output" ||
    ! grep -Fq 'Registry diagnostic suppressed because its workflow-command guard' \
      <<<"$guard_failure_output"; then
  echo 'Image-pin checker exposed registry output after command-guard creation failed' >&2
  printf '%s\n' "$guard_failure_output" >&2
  exit 1
fi

multi_target_result="$work_dir/multi-target-result"
multi_target_expected="$work_dir/multi-target-expected"
printf '%s\n' ubuntu:26.04 ubuntu:22.04 >"$multi_target_expected"

set +e
multi_target_output=$(
  MININGCORE_TEST_INSPECT_FAILURE=1 \
    MININGCORE_IMAGE_PIN_RESULT_FILE="$multi_target_result" \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
multi_target_status=$?
set -e

if [[ "$multi_target_status" -ne 69 ]] ||
    ! cmp -s "$multi_target_expected" "$multi_target_result" ||
    grep -Fq 'Transient image checks unresolved:' <<<"$multi_target_output"; then
  echo 'Image-pin checker did not write the canonical multi-target line contract' >&2
  printf '%s\n' "$multi_target_output" >&2
  exit 1
fi

single_target_result="$work_dir/single-target-result"
single_target_expected="$work_dir/single-target-expected"
printf '%s\n' ubuntu:22.04 >"$single_target_expected"

set +e
single_target_output=$(
  MININGCORE_TEST_TRANSIENT_FAILURE_TAG=ubuntu:22.04 \
    MININGCORE_IMAGE_PIN_RESULT_FILE="$single_target_result" \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
single_target_status=$?
set -e

if [[ "$single_target_status" -ne 69 ]] ||
    ! cmp -s "$single_target_expected" "$single_target_result" ||
    grep -Fq 'Transient image checks unresolved:' <<<"$single_target_output"; then
  echo 'Image-pin checker did not write the canonical one-target line contract' >&2
  printf '%s\n' "$single_target_output" >&2
  exit 1
fi

unwritable_result_path="$work_dir/unwritable-result"
mkdir -p "$unwritable_result_path"

set +e
unwritable_result_output=$(
  MININGCORE_TEST_INSPECT_FAILURE=1 \
    MININGCORE_IMAGE_PIN_RESULT_FILE="$unwritable_result_path" \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
unwritable_result_status=$?
set -e

if [[ "$unwritable_result_status" -ne 70 ]] ||
    ! grep -Fq "Unable to write image-pin result file: $unwritable_result_path" \
      <<<"$unwritable_result_output"; then
  echo 'Image-pin checker misclassified a result-file write failure as drift' >&2
  printf '%s\n' "$unwritable_result_output" >&2
  exit 1
fi

set +e
not_found_output=$(
  MININGCORE_TEST_NOT_FOUND_TAG=ubuntu:22.04 \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
not_found_status=$?
set -e

if [[ "$not_found_status" -ne 70 ]] ||
    ! grep -Fq 'ubuntu:26.04 still matches reviewed pin' <<<"$not_found_output" ||
    ! grep -Fq 'Unable to resolve ubuntu:22.04; the failure was not recognisably transient' \
      <<<"$not_found_output" ||
    ! grep -Fq 'ubuntu:22.04: not found' <<<"$not_found_output"; then
  echo 'Image-pin freshness check downgraded an authoritative missing tag' >&2
  printf '%s\n' "$not_found_output" >&2
  exit 1
fi

set +e
unauthorized_output=$(
  MININGCORE_TEST_UNAUTHORIZED_TAG=ubuntu:22.04 \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
unauthorized_status=$?
set -e

if [[ "$unauthorized_status" -ne 70 ]] ||
    ! grep -Fq 'unauthorized: authentication required' <<<"$unauthorized_output" ||
    grep -Fq 'Transient image checks unresolved:' <<<"$unauthorized_output"; then
  echo 'Image-pin freshness check downgraded an authentication failure' >&2
  printf '%s\n' "$unauthorized_output" >&2
  exit 1
fi

set +e
malformed_output=$(
  MININGCORE_TEST_MALFORMED_INSPECTION=1 \
    PATH="$fake_bin:$PATH" bash "$checker" 2>&1
)
malformed_status=$?
set -e

if [[ "$malformed_status" -ne 70 ]] ||
    ! grep -Fq 'Unable to resolve a manifest-list digest for ubuntu:26.04' \
      <<<"$malformed_output"; then
  echo 'Image-pin freshness check treated malformed resolver output as drift' >&2
  printf '%s\n' "$malformed_output" >&2
  exit 1
fi

missing_bin="$work_dir/missing-bin"
mkdir -p "$missing_bin"
ln -s "$(command -v bash)" "$missing_bin/bash"
ln -s "$(command -v dirname)" "$missing_bin/dirname"

set +e
missing_docker_output=$(PATH="$missing_bin" bash "$checker" 2>&1)
missing_docker_status=$?
set -e

if [[ "$missing_docker_status" -ne 70 ]] ||
    ! grep -Fq 'docker is required' <<<"$missing_docker_output"; then
  echo 'Image-pin freshness check did not fail structurally when docker was missing' >&2
  printf '%s\n' "$missing_docker_output" >&2
  exit 1
fi

for structural_failure in BUILDX IMAGETOOLS; do
  failure_variable="MININGCORE_TEST_${structural_failure}_FAILURE"

  set +e
  structural_output=$(
    env "$failure_variable=1" PATH="$fake_bin:$PATH" bash "$checker" 2>&1
  )
  structural_status=$?
  set -e

  if [[ "$structural_status" -ne 70 ]] ||
      ! grep -Fq 'is required to resolve' <<<"$structural_output"; then
    echo "Image-pin checker downgraded a missing $structural_failure command" >&2
    printf '%s\n' "$structural_output" >&2
    exit 1
  fi

  set +e
  structural_monitor_output=$(
    env "$failure_variable=1" PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
  )
  structural_monitor_status=$?
  set -e

  if [[ "$structural_monitor_status" -ne 70 ]] ||
      grep -Fq '::warning' <<<"$structural_monitor_output" ||
      ! grep -Fq 'is required to resolve' <<<"$structural_monitor_output"; then
    echo "Image-pin monitor downgraded a missing $structural_failure command" >&2
    printf '%s\n' "$structural_monitor_output" >&2
    exit 1
  fi
done

monitor_success_output=$(PATH="$fake_bin:$PATH" bash "$monitor")
if ! grep -Fq 'ubuntu:26.04 still matches reviewed pin' <<<"$monitor_success_output" ||
    ! grep -Fq 'ubuntu:22.04 still matches reviewed pin' <<<"$monitor_success_output"; then
  echo 'Image-pin monitor did not preserve the successful resolver output' >&2
  printf '%s\n' "$monitor_success_output" >&2
  exit 1
fi

set +e
monitor_drift_output=$(
  MININGCORE_TEST_JAMMY_DIGEST=$stale_digest \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
monitor_drift_status=$?
set -e

if [[ "$monitor_drift_status" -ne 1 ]] ||
    ! grep -Fq 'Review upstream changes' <<<"$monitor_drift_output"; then
  echo 'Image-pin monitor did not retain a failing drift signal' >&2
  printf '%s\n' "$monitor_drift_output" >&2
  exit 1
fi

set +e
monitor_registry_output=$(
  MININGCORE_TEST_INSPECT_FAILURE=1 \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
monitor_registry_status=$?
set -e

if [[ "$monitor_registry_status" -ne 0 ]] ||
    ! grep -Fq '::warning title=Ubuntu image pin check unavailable::' \
      <<<"$monitor_registry_output" ||
    ! grep -Fq 'No drift decision for ubuntu:26.04, ubuntu:22.04;' \
      <<<"$monitor_registry_output"; then
  echo 'Image-pin monitor did not downgrade registry failure to a visible warning' >&2
  printf '%s\n' "$monitor_registry_output" >&2
  exit 1
fi

set +e
partial_registry_output=$(
  MININGCORE_TEST_TRANSIENT_FAILURE_TAG=ubuntu:26.04 \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
partial_registry_status=$?
set -e

if [[ "$partial_registry_status" -ne 0 ]] ||
    ! grep -Fq 'ubuntu:22.04 still matches reviewed pin' \
      <<<"$partial_registry_output" ||
    ! grep -Fq 'No drift decision for ubuntu:26.04;' \
      <<<"$partial_registry_output" ||
    grep -Fq 'No drift decision for ubuntu:26.04, ubuntu:22.04' \
      <<<"$partial_registry_output" ||
    grep -Fq 'Transient image checks unresolved:' \
      <<<"$partial_registry_output"; then
  echo 'Image-pin monitor did not identify only the unresolved image target' >&2
  printf '%s\n' "$partial_registry_output" >&2
  exit 1
fi

partial_failure_line=$(
  grep -nF 'Transient registry failure while resolving ubuntu:26.04' \
    <<<"$partial_registry_output" | head -n 1
)
partial_success_line=$(
  grep -nF 'ubuntu:22.04 still matches reviewed pin' \
    <<<"$partial_registry_output" | head -n 1
)

if (( ${partial_failure_line%%:*} >= ${partial_success_line%%:*} )); then
  echo 'Image-pin monitor reordered target diagnostics while capturing its result' >&2
  printf '%s\n' "$partial_registry_output" >&2
  exit 1
fi

set +e
internal_server_error_output=$(
  MININGCORE_TEST_INTERNAL_SERVER_ERROR_TAG=ubuntu:22.04 \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
internal_server_error_status=$?
set -e

if [[ "$internal_server_error_status" -ne 0 ]] ||
    ! grep -Fq 'No drift decision for ubuntu:22.04;' \
      <<<"$internal_server_error_output" ||
    ! grep -Fq '500 Internal Server Error' \
      <<<"$internal_server_error_output"; then
  echo 'Image-pin monitor did not classify a registry HTTP 500 as transient' >&2
  printf '%s\n' "$internal_server_error_output" >&2
  exit 1
fi

set +e
monitor_unauthorized_output=$(
  MININGCORE_TEST_UNAUTHORIZED_TAG=ubuntu:22.04 \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
monitor_unauthorized_status=$?
set -e

if [[ "$monitor_unauthorized_status" -ne 70 ]] ||
    grep -Fq '::warning' <<<"$monitor_unauthorized_output" ||
    ! grep -Fq 'unauthorized: authentication required' \
      <<<"$monitor_unauthorized_output"; then
  echo 'Image-pin monitor downgraded an authentication failure' >&2
  printf '%s\n' "$monitor_unauthorized_output" >&2
  exit 1
fi

set +e
monitor_not_found_output=$(
  MININGCORE_TEST_NOT_FOUND_TAG=ubuntu:22.04 \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
monitor_not_found_status=$?
set -e

if [[ "$monitor_not_found_status" -ne 70 ]] ||
    grep -Fq '::warning' <<<"$monitor_not_found_output" ||
    ! grep -Fq 'ubuntu:22.04: not found' <<<"$monitor_not_found_output"; then
  echo 'Image-pin monitor downgraded an authoritative missing tag' >&2
  printf '%s\n' "$monitor_not_found_output" >&2
  exit 1
fi

set +e
mixed_output=$(
  MININGCORE_TEST_TRANSIENT_FAILURE_TAG=ubuntu:26.04 \
    MININGCORE_TEST_JAMMY_DIGEST=$stale_digest \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
mixed_status=$?
set -e

if [[ "$mixed_status" -ne 1 ]] ||
    grep -Fq '::warning' <<<"$mixed_output" ||
    ! grep -Fq 'Transient registry failure while resolving ubuntu:26.04' \
      <<<"$mixed_output" ||
    ! grep -Fq 'ubuntu:22.04 now resolves to sha256:111111' <<<"$mixed_output"; then
  echo 'Image-pin monitor allowed one target outage to suppress another target drift' >&2
  printf '%s\n' "$mixed_output" >&2
  exit 1
fi

set +e
monitor_malformed_output=$(
  MININGCORE_TEST_MALFORMED_INSPECTION=1 \
    PATH="$fake_bin:$PATH" bash "$monitor" 2>&1
)
monitor_malformed_status=$?
set -e

if [[ "$monitor_malformed_status" -ne 70 ]] ||
    grep -Fq '::warning' <<<"$monitor_malformed_output" ||
    ! grep -Fq 'Unable to resolve a manifest-list digest' \
      <<<"$monitor_malformed_output"; then
  echo 'Image-pin monitor downgraded a structural resolver failure' >&2
  printf '%s\n' "$monitor_malformed_output" >&2
  exit 1
fi

monitor_contract_root="$work_dir/monitor-contract"
monitor_contract_scripts="$monitor_contract_root/scripts/release"
mkdir -p "$monitor_contract_scripts"
cp "$monitor" "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh"
cp "$repository_root/scripts/release/linux-release-targets.sh" \
  "$monitor_contract_scripts/linux-release-targets.sh"

failing_mktemp_bin="$work_dir/failing-mktemp-bin"
mkdir -p "$failing_mktemp_bin"

cat > "$failing_mktemp_bin/mktemp" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
chmod 0755 "$failing_mktemp_bin/mktemp"

set +e
mktemp_failure_output=$(
  PATH="$failing_mktemp_bin:$PATH" \
    bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
mktemp_failure_status=$?
set -e

if [[ "$mktemp_failure_status" -ne 70 ]] ||
    grep -Fq '::warning' <<<"$mktemp_failure_output" ||
    ! grep -Fq 'Unable to create private image-pin result file' \
      <<<"$mktemp_failure_output"; then
  echo 'Image-pin monitor misclassified result-file creation failure as drift' >&2
  printf '%s\n' "$mktemp_failure_output" >&2
  exit 1
fi

cat > "$monitor_contract_scripts/check-linux-release-image-pins.sh" <<'EOF'
#!/usr/bin/env bash
echo 'injected transient checker noise' >&2
exit 69
EOF

set +e
missing_summary_output=$(
  bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
missing_summary_status=$?
set -e

if [[ "$missing_summary_status" -ne 70 ]] ||
    grep -Fq '::warning' <<<"$missing_summary_output" ||
    ! grep -Fq 'injected transient checker noise' <<<"$missing_summary_output" ||
    ! grep -Fq 'transient status without a non-empty unresolved-target result' \
      <<<"$missing_summary_output"; then
  echo 'Image-pin monitor downgraded a broken transient-result handoff' >&2
  printf '%s\n' "$missing_summary_output" >&2
  exit 1
fi

cat > "$monitor_contract_scripts/check-linux-release-image-pins.sh" <<'EOF'
#!/usr/bin/env bash
rm -f -- "$MININGCORE_IMAGE_PIN_RESULT_FILE"
ln -s "$MININGCORE_IMAGE_PIN_RESULT_FILE.missing" \
  "$MININGCORE_IMAGE_PIN_RESULT_FILE"
exit 69
EOF

set +e
unreadable_summary_output=$(
  bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
unreadable_summary_status=$?
set -e

if [[ "$unreadable_summary_status" -ne 70 ]] ||
    grep -Fq '::warning' <<<"$unreadable_summary_output" ||
    ! grep -Fq 'Unable to read image-pin result file:' \
      <<<"$unreadable_summary_output"; then
  echo 'Image-pin monitor misclassified a result-file read failure as drift' >&2
  printf '%s\n' "$unreadable_summary_output" >&2
  exit 1
fi

cat > "$monitor_contract_scripts/check-linux-release-image-pins.sh" <<'EOF'
#!/usr/bin/env bash
if [[ ${MININGCORE_TEST_HANDOFF_NUL:-} = 1 ]]; then
  printf 'ubuntu:26.04\0\n' >"$MININGCORE_IMAGE_PIN_RESULT_FILE"
else
  printf '%s' "${MININGCORE_TEST_HANDOFF_CONTENT:-}" \
    >"$MININGCORE_IMAGE_PIN_RESULT_FILE"
fi
exit 69
EOF

assert_valid_handoff() {
  local label=$1
  local content=$2
  local expected_targets=$3
  local output
  local status

  set +e
  output=$(
    MININGCORE_TEST_HANDOFF_CONTENT="$content" \
      bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
  )
  status=$?
  set -e

  if [[ "$status" -ne 0 ]] ||
      ! grep -Fq \
        "No drift decision for $expected_targets; all configured targets were evaluated." \
        <<<"$output"; then
    echo "Image-pin monitor rejected valid line-oriented handoff: $label" >&2
    printf '%s\n' "$output" >&2
    exit 1
  fi
}

assert_invalid_handoff() {
  local label=$1
  local content=$2
  local expected_diagnostic=$3
  local output
  local status

  set +e
  output=$(
    MININGCORE_TEST_HANDOFF_CONTENT="$content" \
      bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
  )
  status=$?
  set -e

  if [[ "$status" -ne 70 ]] ||
      grep -Fq '::warning' <<<"$output" ||
      ! grep -Fq "$expected_diagnostic" <<<"$output"; then
    echo "Image-pin monitor accepted invalid line-oriented handoff: $label" >&2
    printf '%s\n' "$output" >&2
    exit 1
  fi
}

assert_valid_handoff \
  'first configured target' \
  $'ubuntu:26.04\n' \
  'ubuntu:26.04'
assert_valid_handoff \
  'later configured target' \
  $'ubuntu:22.04\n' \
  'ubuntu:22.04'
assert_valid_handoff \
  'ordered multi-target subset' \
  $'ubuntu:26.04\nubuntu:22.04\n' \
  'ubuntu:26.04, ubuntu:22.04'

empty_result_diagnostic='transient status without a non-empty unresolved-target result'
invalid_target_diagnostic='non-canonical, unknown, duplicate, or out-of-order unresolved target'

assert_invalid_handoff 'empty file' '' "$empty_result_diagnostic"
assert_invalid_handoff 'blank line' $'\n' "$invalid_target_diagnostic"
assert_invalid_handoff \
  'blank line between targets' \
  $'ubuntu:26.04\n\nubuntu:22.04\n' \
  'out-of-order unresolved target at line 2'
assert_invalid_handoff \
  'leading whitespace' \
  $' ubuntu:26.04\n' \
  "$invalid_target_diagnostic"
assert_invalid_handoff \
  'trailing whitespace' \
  $'ubuntu:26.04 \n' \
  "$invalid_target_diagnostic"
assert_invalid_handoff \
  'carriage return' \
  $'ubuntu:26.04\r\n' \
  "$invalid_target_diagnostic"
assert_invalid_handoff 'unknown tag' $'ubuntu:24.04\n' "$invalid_target_diagnostic"
assert_invalid_handoff \
  'duplicate tag' \
  $'ubuntu:26.04\nubuntu:26.04\n' \
  "$invalid_target_diagnostic"
assert_invalid_handoff \
  'reversed tags' \
  $'ubuntu:22.04\nubuntu:26.04\n' \
  "$invalid_target_diagnostic"
assert_invalid_handoff \
  'overlong result' \
  $'ubuntu:26.04\nubuntu:22.04\nubuntu:26.04\n' \
  'out-of-order unresolved target at line 3'
assert_invalid_handoff \
  'legacy joined payload' \
  $'ubuntu:26.04, ubuntu:22.04\n' \
  "$invalid_target_diagnostic"
assert_invalid_handoff \
  'missing terminal newline' \
  'ubuntu:26.04' \
  'does not exactly match the canonical line-oriented contract'

set +e
nul_handoff_output=$(
  MININGCORE_TEST_HANDOFF_NUL=1 \
    bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
nul_handoff_status=$?
set -e

if [[ "$nul_handoff_status" -ne 70 ]] ||
    grep -Fq '::warning' <<<"$nul_handoff_output" ||
    ! grep -Fq 'does not exactly match the canonical line-oriented contract' \
      <<<"$nul_handoff_output"; then
  echo 'Image-pin monitor accepted NUL-contaminated handoff data' >&2
  printf '%s\n' "$nul_handoff_output" >&2
  exit 1
fi

injected_annotation='::warning title=Injected annotation payload::untrusted data'
assert_invalid_handoff \
  'workflow-command injection' \
  "$injected_annotation"$'\n' \
  "$invalid_target_diagnostic"

set +e
injected_annotation_output=$(
  MININGCORE_TEST_HANDOFF_CONTENT="$injected_annotation"$'\n' \
    bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
injected_annotation_status=$?
set -e

if [[ "$injected_annotation_status" -ne 70 ]] ||
    grep -Fq "$injected_annotation" <<<"$injected_annotation_output"; then
  echo 'Image-pin monitor allowed raw handoff content to reach its output' >&2
  printf '%s\n' "$injected_annotation_output" >&2
  exit 1
fi

large_contract_root="$work_dir/large-monitor-contract"
large_contract_scripts="$large_contract_root/scripts/release"
mkdir -p "$large_contract_scripts"
cp "$monitor" "$large_contract_scripts/run-linux-release-image-pin-monitor.sh"
cp "$monitor_contract_scripts/check-linux-release-image-pins.sh" \
  "$large_contract_scripts/check-linux-release-image-pins.sh"

cat > "$large_contract_scripts/linux-release-targets.sh" <<'EOF'
#!/usr/bin/env bash
readonly MININGCORE_LINUX_RELEASE_TARGETS=(26.04 24.04 22.04 20.04)

miningcore_linux_release_target_image() {
  printf 'ubuntu:%s@sha256:%064d\n' "$1" 0
}
EOF

set +e
large_contract_output=$(
  MININGCORE_TEST_HANDOFF_CONTENT=$'ubuntu:26.04\nubuntu:22.04\nubuntu:20.04\n' \
    bash "$large_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
large_contract_status=$?
set -e

if [[ "$large_contract_status" -ne 0 ]] ||
    ! grep -Fq 'No drift decision for ubuntu:26.04, ubuntu:22.04, ubuntu:20.04;' \
      <<<"$large_contract_output"; then
  echo 'Image-pin monitor rejected an ordered subset of a larger target contract' >&2
  printf '%s\n' "$large_contract_output" >&2
  exit 1
fi

set +e
large_contract_blank_output=$(
  MININGCORE_TEST_HANDOFF_CONTENT=$'ubuntu:26.04\n\nubuntu:22.04\n' \
    bash "$large_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
large_contract_blank_status=$?
set -e

if [[ "$large_contract_blank_status" -ne 70 ]] ||
    ! grep -Fq 'out-of-order unresolved target at line 2' \
      <<<"$large_contract_blank_output"; then
  echo 'Image-pin monitor did not reject a blank line under a larger target contract' >&2
  printf '%s\n' "$large_contract_blank_output" >&2
  exit 1
fi

cat > "$monitor_contract_scripts/check-linux-release-image-pins.sh" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' 'ubuntu:26.04' >"$MININGCORE_IMAGE_PIN_RESULT_FILE"
echo 'injected diagnostic after result publication' >&2
exit 69
EOF

set +e
ordered_summary_output=$(
  bash "$monitor_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
ordered_summary_status=$?
set -e

if [[ "$ordered_summary_status" -ne 0 ]] ||
    ! grep -Fq 'injected diagnostic after result publication' \
      <<<"$ordered_summary_output" ||
    ! grep -Fq 'No drift decision for ubuntu:26.04;' \
      <<<"$ordered_summary_output"; then
  echo 'Image-pin monitor coupled its transient handoff to diagnostic ordering' >&2
  printf '%s\n' "$ordered_summary_output" >&2
  exit 1
fi

for expected in \
  "'scripts/release/run-linux-release-image-pin-monitor.sh'" \
  'shellcheck -x' \
  'run: bash scripts/release/run-linux-release-image-pin-monitor.sh'; do
  if ! grep -Fq "$expected" "$workflow"; then
    echo "Image-pin workflow is missing monitor contract: $expected" >&2
    exit 1
  fi
done

for expected in \
  'workflow-command processing is suspended' \
  'unresolved canonical image tag per line in central release-target order' \
  'accepts only a non-empty, unique, in-order subset of the configured tags' \
  'configured-target-plus-one bound' \
  'locally derived line number' \
  'readable, comma-separated summary on stderr'; do
  if ! grep -Fq "$expected" "$release_docs"; then
    echo "Release documentation is missing image-pin result contract: $expected" >&2
    exit 1
  fi
done

echo 'Linux release image-pin freshness checks passed'
