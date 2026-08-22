#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
checker="$repository_root/scripts/release/check-linux-release-image-pins.sh"
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

echo 'Linux release image-pin freshness checks passed'
