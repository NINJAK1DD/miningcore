#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
monitor="$repository_root/scripts/release/run-linux-release-image-pin-monitor.sh"
workflow="$repository_root/.github/workflows/release-image-pins.yml"
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

if [[ "$#" -ne 4 || "$1" != buildx || "$2" != imagetools || "$3" != inspect ]]; then
  echo "Unexpected docker invocation: $*" >&2
  exit 64
fi

if [[ ${MININGCORE_TEST_INSPECT_FAILURE:-} = 1 ]]; then
  echo 'injected registry failure' >&2
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
    ! grep -Fq 'Unable to reach the registry while resolving ubuntu:26.04' \
      <<<"$registry_output" ||
    ! grep -Fq 'injected registry failure' <<<"$registry_output"; then
  echo 'Image-pin freshness check did not distinguish registry failure from drift' >&2
  printf '%s\n' "$registry_output" >&2
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
    ! grep -Fq 'no image drift decision was made' <<<"$monitor_registry_output"; then
  echo 'Image-pin monitor did not downgrade registry failure to a visible warning' >&2
  printf '%s\n' "$monitor_registry_output" >&2
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

for expected in \
  "'scripts/release/run-linux-release-image-pin-monitor.sh'" \
  'run: bash scripts/release/run-linux-release-image-pin-monitor.sh'; do
  if ! grep -Fq "$expected" "$workflow"; then
    echo "Image-pin workflow is missing monitor contract: $expected" >&2
    exit 1
  fi
done

echo 'Linux release image-pin freshness checks passed'
