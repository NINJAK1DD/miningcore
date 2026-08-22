#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
monitor="$repository_root/scripts/release/run-linux-release-image-pin-monitor.sh"
workflow="$repository_root/.github/workflows/release-image-pins.yml"
dotnet_workflow="$repository_root/.github/workflows/dotnet.yml"
release_docs="$repository_root/docs/releases.md"
source "$repository_root/scripts/release/linux-release-targets.sh"
work_dir=$(mktemp -d)
fake_bin="$work_dir/bin"
mkdir -p "$fake_bin"

cleanup() {
  rm -rf -- "$work_dir"
}
trap cleanup EXIT

print_test_diagnostic() {
  local diagnostic=$1
  local encoded_diagnostic

  # Keep adversarial failure evidence recoverable without replaying either Actions command syntax.
  # First collapse CR/LF with %q, then rewrite both V2 and legacy command sentinels.
  printf -v encoded_diagnostic '%q' "$diagnostic"
  encoded_diagnostic=${encoded_diagnostic//'::'/:<colon>}
  encoded_diagnostic=${encoded_diagnostic//'##['/'##<left-bracket>'}
  printf 'Test diagnostic (encoded): %s\n' "$encoded_diagnostic" >&2
}

contains_runner_command() {
  local output=$1
  local line
  local trimmed

  # ProcessInvoker reads CR, LF and CRLF as line endings. Model that boundary, then mirror the V2
  # parser's leading-whitespace trim and conservatively reject every legacy "##[" sentinel.
  while IFS= read -r line || [[ -n "$line" ]]; do
    trimmed=$line

    while [[ "$trimmed" = [[:space:]]* ]]; do
      trimmed=${trimmed:1}
    done

    if [[ "$trimmed" = ::* || "$line" = *'##['* ]]; then
      return 0
    fi
  done < <(printf '%s' "$output" | tr '\r' '\n')

  return 1
}

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
  printf '  ::error title=Injected by registry::pwned\r' >&2
  printf 'noise ##[error]legacy-v1-form\r' >&2
  printf '::add-mask::injected-secret\n' >&2
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
  print_test_diagnostic "$failure_output"
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
  print_test_diagnostic "$registry_output"
  exit 1
fi

all_failure_stdout="$work_dir/all-failure.stdout"
all_failure_stderr="$work_dir/all-failure.stderr"

set +e
GITHUB_ACTIONS=true \
  MININGCORE_TEST_INSPECT_FAILURE=1 \
  PATH="$fake_bin:$PATH" bash "$checker" \
  >"$all_failure_stdout" 2>"$all_failure_stderr"
all_failure_status=$?
set -e

all_failure_output=$(<"$all_failure_stdout")
all_failure_error=$(<"$all_failure_stderr")
mapfile -t all_failure_tokens < <(
  sed -n 's/^::stop-commands::\([0-9a-f]\{32\}\)$/\1/p' \
    <<<"$all_failure_output"
)

if [[ "$all_failure_status" -ne 69 ]] ||
    (( ${#all_failure_tokens[@]} != ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} )) ||
    grep -Fq 'Transient registry failure while resolving' <<<"$all_failure_error"; then
  echo 'Image-pin checker split multi-target headers from their guarded evidence' >&2
  print_test_diagnostic "$all_failure_output"
  print_test_diagnostic "$all_failure_error"
  exit 1
fi

previous_resume_line=0

for target_index in "${!MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
  target=${MININGCORE_LINUX_RELEASE_TARGETS[$target_index]}
  token=${all_failure_tokens[$target_index]}
  header_line=$(awk -v needle="Transient registry failure while resolving ubuntu:$target" \
    'index($0, needle) { print NR; exit }' <<<"$all_failure_output")
  stop_line=$(awk -v needle="::stop-commands::$token" \
    'index($0, needle) { print NR; exit }' <<<"$all_failure_output")
  resume_line=$(awk -v needle="::$token::" \
    'index($0, needle) { print NR; exit }' <<<"$all_failure_output")

  if [[ -z "$header_line" || -z "$stop_line" || -z "$resume_line" ]] ||
      (( previous_resume_line >= header_line || header_line >= stop_line ||
        stop_line >= resume_line )); then
    echo 'Image-pin checker reordered multi-target headers and guarded evidence' >&2
    print_test_diagnostic "$all_failure_output"
    exit 1
  fi

  previous_resume_line=$resume_line
done

workflow_command_stdout="$work_dir/workflow-command.stdout"
workflow_command_stderr="$work_dir/workflow-command.stderr"

set +e
GITHUB_ACTIONS=true \
  MININGCORE_TEST_WORKFLOW_COMMAND_TAG=ubuntu:26.04 \
  PATH="$fake_bin:$PATH" bash "$checker" \
  >"$workflow_command_stdout" 2>"$workflow_command_stderr"
workflow_command_status=$?
set -e

workflow_command_output=$(<"$workflow_command_stdout")
workflow_command_error=$(<"$workflow_command_stderr")

workflow_command='::error title=Injected by registry::pwned'
workflow_legacy_command='noise ##[error]legacy-v1-form'
workflow_cr_command='::add-mask::injected-secret'
workflow_guard_token=$(
  sed -n 's/^::stop-commands::\([0-9a-f]\{32\}\)$/\1/p' \
    <<<"$workflow_command_output" | head -n 1
)

if [[ "$workflow_command_status" -ne 69 ]] ||
    [[ ! "$workflow_guard_token" =~ ^[0-9a-f]{32}$ ]] ||
    grep -Fq "$workflow_command" <<<"$workflow_command_error" ||
    grep -Fq "$workflow_legacy_command" <<<"$workflow_command_error" ||
    grep -Fq "$workflow_cr_command" <<<"$workflow_command_error"; then
  echo 'Image-pin checker did not guard registry output on the Actions command stream' >&2
  print_test_diagnostic "$workflow_command_output"
  print_test_diagnostic "$workflow_command_error"
  exit 1
fi

workflow_header_line=$(awk \
  'index($0, "Transient registry failure while resolving ubuntu:26.04") { print NR; exit }' \
  <<<"$workflow_command_output")
workflow_stop_line=$(awk -v needle="::stop-commands::$workflow_guard_token" \
  'index($0, needle) { print NR; exit }' <<<"$workflow_command_output")
workflow_payload_line=$(awk -v needle="$workflow_command" \
  'index($0, needle) { print NR; exit }' <<<"$workflow_command_output")
workflow_resume_line=$(awk -v needle="::$workflow_guard_token::" \
  'index($0, needle) { print NR; exit }' <<<"$workflow_command_output")

if [[ -z "$workflow_header_line" || -z "$workflow_stop_line" || -z "$workflow_payload_line" ||
    -z "$workflow_resume_line" ]] ||
    (( workflow_header_line >= workflow_stop_line ||
      workflow_stop_line >= workflow_payload_line ||
      workflow_payload_line >= workflow_resume_line )); then
  echo 'Image-pin checker did not keep its header and evidence ordered on stdout' >&2
  print_test_diagnostic "$workflow_command_output"
  print_test_diagnostic "$workflow_command_error"
  exit 1
fi

monitor_command_stdout="$work_dir/monitor-command.stdout"
monitor_command_stderr="$work_dir/monitor-command.stderr"

set +e
GITHUB_ACTIONS=true \
  MININGCORE_TEST_WORKFLOW_COMMAND_TAG=ubuntu:26.04 \
  PATH="$fake_bin:$PATH" bash "$monitor" \
  >"$monitor_command_stdout" 2>"$monitor_command_stderr"
monitor_command_status=$?
set -e

monitor_command_output=$(<"$monitor_command_stdout")
monitor_command_error=$(<"$monitor_command_stderr")
monitor_guard_token=$(
  sed -n 's/^::stop-commands::\([0-9a-f]\{32\}\)$/\1/p' \
    <<<"$monitor_command_output" | head -n 1
)

if [[ "$monitor_command_status" -ne 0 ]] ||
    [[ ! "$monitor_guard_token" =~ ^[0-9a-f]{32}$ ]] ||
    grep -Fq "$workflow_command" <<<"$monitor_command_error" ||
    grep -Fq "$workflow_legacy_command" <<<"$monitor_command_error" ||
    grep -Fq "$workflow_cr_command" <<<"$monitor_command_error"; then
  echo 'Image-pin monitor did not keep guarded output and its warning on one stream' >&2
  print_test_diagnostic "$monitor_command_output"
  print_test_diagnostic "$monitor_command_error"
  exit 1
fi

monitor_header_line=$(awk \
  'index($0, "Transient registry failure while resolving ubuntu:26.04") { print NR; exit }' \
  <<<"$monitor_command_output")
monitor_stop_line=$(awk -v needle="::stop-commands::$monitor_guard_token" \
  'index($0, needle) { print NR; exit }' <<<"$monitor_command_output")
monitor_payload_line=$(awk -v needle="$workflow_command" \
  'index($0, needle) { print NR; exit }' <<<"$monitor_command_output")
monitor_resume_line=$(awk -v needle="::$monitor_guard_token::" \
  'index($0, needle) { print NR; exit }' <<<"$monitor_command_output")
monitor_warning_line=$(awk \
  'index($0, "::warning title=Ubuntu image pin check unavailable::") { print NR; exit }' \
  <<<"$monitor_command_output")

if [[ -z "$monitor_header_line" || -z "$monitor_stop_line" || -z "$monitor_payload_line" ||
    -z "$monitor_resume_line" || -z "$monitor_warning_line" ]] ||
    (( monitor_header_line >= monitor_stop_line ||
      monitor_stop_line >= monitor_payload_line ||
      monitor_payload_line >= monitor_resume_line ||
      monitor_resume_line >= monitor_warning_line )); then
  echo 'Image-pin monitor did not order stop, payload, resume, and warning deterministically' >&2
  print_test_diagnostic "$monitor_command_output"
  print_test_diagnostic "$monitor_command_error"
  exit 1
fi

guard_failure_bin="$work_dir/guard-failure-bin"
mkdir -p "$guard_failure_bin"

cat > "$guard_failure_bin/od" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
chmod 0755 "$guard_failure_bin/od"

guard_failure_stdout="$work_dir/guard-failure.stdout"
guard_failure_stderr="$work_dir/guard-failure.stderr"

set +e
GITHUB_ACTIONS=true \
  MININGCORE_TEST_WORKFLOW_COMMAND_TAG=ubuntu:26.04 \
  PATH="$guard_failure_bin:$fake_bin:$PATH" bash "$checker" \
  >"$guard_failure_stdout" 2>"$guard_failure_stderr"
guard_failure_status=$?
set -e

guard_failure_output=$(<"$guard_failure_stdout")
guard_failure_error=$(<"$guard_failure_stderr")

if [[ "$guard_failure_status" -ne 69 ]] ||
    contains_runner_command "$guard_failure_output" ||
    contains_runner_command "$guard_failure_error" ||
    ! grep -Fq 'Registry diagnostic (encoded; command guard unavailable):' \
      <<<"$guard_failure_output" ||
    ! grep -Fq 'legacy-v1-form' <<<"$guard_failure_output" ||
    ! grep -Fq 'injected-secret' <<<"$guard_failure_output"; then
  echo 'Image-pin checker exposed a command after its guard could not be created' >&2
  print_test_diagnostic "$guard_failure_output"
  print_test_diagnostic "$guard_failure_error"
  exit 1
fi

hostile_test_diagnostic=$'  ::error title=fixture::v2\rnoise ##[error]v1\r::add-mask::secret'
safe_test_dump=$(print_test_diagnostic "$hostile_test_diagnostic" 2>&1)

if contains_runner_command "$safe_test_dump" ||
    ! grep -Fq 'Test diagnostic (encoded):' <<<"$safe_test_dump" ||
    ! grep -Fq 'fixture' <<<"$safe_test_dump" ||
    ! grep -Fq 'add-mask' <<<"$safe_test_dump"; then
  echo 'Image-pin test diagnostics can replay a hostile workflow command' >&2
  print_test_diagnostic "$safe_test_dump"
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
  print_test_diagnostic "$multi_target_output"
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
  print_test_diagnostic "$single_target_output"
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
    ! grep -Fq 'Unable to write private image-pin result file' \
      <<<"$unwritable_result_output"; then
  echo 'Image-pin checker misclassified a result-file write failure as drift' >&2
  print_test_diagnostic "$unwritable_result_output"
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
  print_test_diagnostic "$not_found_output"
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
  print_test_diagnostic "$unauthorized_output"
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
      <<<"$malformed_output" ||
    ! grep -Fq 'MediaType: application/vnd.oci.image.index.v1+json' \
      <<<"$malformed_output"; then
  echo 'Image-pin freshness check treated malformed resolver output as drift' >&2
  print_test_diagnostic "$malformed_output"
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
  print_test_diagnostic "$missing_docker_output"
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
    print_test_diagnostic "$structural_output"
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
    print_test_diagnostic "$structural_monitor_output"
    exit 1
  fi
done

monitor_success_output=$(PATH="$fake_bin:$PATH" bash "$monitor")
if ! grep -Fq 'ubuntu:26.04 still matches reviewed pin' <<<"$monitor_success_output" ||
    ! grep -Fq 'ubuntu:22.04 still matches reviewed pin' <<<"$monitor_success_output"; then
  echo 'Image-pin monitor did not preserve the successful resolver output' >&2
  print_test_diagnostic "$monitor_success_output"
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
  print_test_diagnostic "$monitor_drift_output"
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
  print_test_diagnostic "$monitor_registry_output"
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
  print_test_diagnostic "$partial_registry_output"
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
  print_test_diagnostic "$partial_registry_output"
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
  print_test_diagnostic "$internal_server_error_output"
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
  print_test_diagnostic "$monitor_unauthorized_output"
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
  print_test_diagnostic "$monitor_not_found_output"
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
  print_test_diagnostic "$mixed_output"
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
  print_test_diagnostic "$monitor_malformed_output"
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
  print_test_diagnostic "$mktemp_failure_output"
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
  print_test_diagnostic "$missing_summary_output"
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
    ! grep -Fq 'Unable to read private image-pin result file' \
      <<<"$unreadable_summary_output" ||
    grep -Fq "$work_dir" <<<"$unreadable_summary_output"; then
  echo 'Image-pin monitor misclassified a result-file read failure as drift' >&2
  print_test_diagnostic "$unreadable_summary_output"
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
    print_test_diagnostic "$output"
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
    print_test_diagnostic "$output"
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
overlong_handoff=''
for target in "${MININGCORE_LINUX_RELEASE_TARGETS[@]}"; do
  image=$(miningcore_linux_release_target_image "$target")
  overlong_handoff+="${image%@*}"$'\n'
done

first_image=$(miningcore_linux_release_target_image \
  "${MININGCORE_LINUX_RELEASE_TARGETS[0]}")
first_tag=${first_image%@*}
for ((extra_line = 0; extra_line < 50; extra_line++)); do
  overlong_handoff+="$first_tag"$'\n'
done

assert_invalid_handoff \
  'many-line result exceeding the configured contract' \
  "$overlong_handoff" \
  "out-of-order unresolved target at line $(( \
    ${#MININGCORE_LINUX_RELEASE_TARGETS[@]} + 1 ))"
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
  print_test_diagnostic "$nul_handoff_output"
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
  print_test_diagnostic "$injected_annotation_output"
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
  print_test_diagnostic "$large_contract_output"
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
  print_test_diagnostic "$large_contract_blank_output"
  exit 1
fi

large_contract_overlong_content=$'ubuntu:26.04\nubuntu:24.04\nubuntu:22.04\n'
large_contract_overlong_content+=$'ubuntu:20.04\nubuntu:26.04\n'

set +e
large_contract_overlong_output=$(
  MININGCORE_TEST_HANDOFF_CONTENT="$large_contract_overlong_content" \
    bash "$large_contract_scripts/run-linux-release-image-pin-monitor.sh" 2>&1
)
large_contract_overlong_status=$?
set -e

if [[ "$large_contract_overlong_status" -ne 70 ]] ||
    ! grep -Fq 'out-of-order unresolved target at line 5' \
      <<<"$large_contract_overlong_output"; then
  echo 'Image-pin monitor did not bound a larger target contract at N+1 lines' >&2
  print_test_diagnostic "$large_contract_overlong_output"
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
  print_test_diagnostic "$ordered_summary_output"
  exit 1
fi

for expected in \
  "'scripts/release/run-linux-release-image-pin-monitor.sh'" \
  'run: bash scripts/release/run-linux-release-image-pin-monitor.sh'; do
  if ! grep -Fq "$expected" "$workflow"; then
    echo "Image-pin workflow is missing monitor contract: $expected" >&2
    exit 1
  fi
done

if grep -Fq 'shellcheck' "$workflow"; then
  echo 'Scheduled image-pin monitoring must run independently of lint tooling' >&2
  exit 1
fi

if ! grep -Fq 'shellcheck -x' "$dotnet_workflow"; then
  echo 'Always-running .NET CI no longer enforces release-script ShellCheck' >&2
  exit 1
fi

for expected in \
  'workflow-command processing is suspended' \
  'shell-escaped physical line' \
  'unresolved canonical image tag per line in central release-target order' \
  'accepts only a non-empty, unique, in-order subset of the configured tags' \
  'configured target count plus one line' \
  'locally derived line number' \
  'readable, comma-separated summary on stderr'; do
  if ! grep -Fq "$expected" "$release_docs"; then
    echo "Release documentation is missing image-pin result contract: $expected" >&2
    exit 1
  fi
done

echo 'Linux release image-pin freshness checks passed'
