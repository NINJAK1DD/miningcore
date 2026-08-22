#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
document="$repository_root/docs/releases.md"
selection_block=$(awk '
  { sub(/\r$/, "") }
  /^export MININGCORE_VERSION=/ { capture = 1 }
  capture && /^```$/ { exit }
  capture { print }
' "$document")
install_block=$(awk '
  { sub(/\r$/, "") }
  /^## Install the archive$/ { section = 1; next }
  section && /^```console$/ { capture = 1; next }
  capture && /^```$/ { exit }
  capture { print }
' "$document")

assert_contains() {
  local label=$1
  local expected=$2

  if ! grep -Fq "$expected" "$document"; then
    echo "Release installation guide is missing $label" >&2
    exit 1
  fi
}

assert_contains 'the Ubuntu 26.04 choose-one label' \
  '(choose this on Ubuntu 26.04)'
assert_contains 'the Ubuntu 22.04 choose-one label' \
  '(choose this on Ubuntu 22.04)'
assert_contains 'the interactive-shell safety explanation' \
  'instead of closing an SSH session'
assert_contains 'the successful verification marker' \
  'READY: $archive is verified and ready to install'
assert_contains 'the all-jobs release retry rule' \
  'select **Re-run all jobs**'
assert_contains 'the failed-jobs retry prohibition' \
  'Do not use **Re-run failed jobs**'
assert_contains 'the declared build-image contract' \
  'workflow-declared'
assert_contains 'the Ubuntu 22.04 curl compatibility statement' \
  'curl version supplied by Ubuntu 22.04'

if grep -Eq '(^|[[:space:]])exit([[:space:]]|$)' <<<"$selection_block"; then
  echo "The copy-paste release selection block must not exit an interactive shell" >&2
  exit 1
fi

bash -n <<<"$selection_block"
bash -n <<<"$install_block"

for required in \
  'MININGCORE_HOST_RELEASE=' \
  'MININGCORE_RELEASE_READY=' \
  'MININGCORE_DOWNLOAD_DIR=' \
  'if [ -n "$MININGCORE_UBUNTU" ]; then' \
  'if MININGCORE_DOWNLOAD_DIR="$(' \
  'mktemp -d "${TMPDIR:-/tmp}/miningcore-release.XXXXXXXX"' \
  'curl --fail --location --output "$archive_part"' \
  'curl --fail --location --output "$checksum_part"' \
  'sha256sum --ignore-missing --check --strict SHA256SUMS' \
  'export MININGCORE_RELEASE_READY=1' \
  'rmdir -- "$MININGCORE_DOWNLOAD_DIR"' \
  'archive='; do
  if ! grep -Fq "$required" <<<"$selection_block"; then
    echo "Release selection block is missing: $required" >&2
    exit 1
  fi
done

if grep -Fq -- '--remove-on-error' <<<"$selection_block" ||
    grep -Fq -- '--remote-name' <<<"$selection_block"; then
  echo "Release selection block uses a curl option outside the Ubuntu 22.04 contract" >&2
  exit 1
fi

# This test also runs inside each target's pinned release-build userspace. Keep
# every advertised curl option available on the oldest supported Ubuntu target.
curl_help=$(curl --help all)
for curl_option in --fail --location --output; do
  if ! grep -Eq "(^|[[:space:]])${curl_option}([[:space:]=,]|$)" <<<"$curl_help"; then
    echo "The current curl does not support documented option $curl_option" >&2
    exit 1
  fi
done

for required in \
  'if [ "${MININGCORE_RELEASE_READY:-}" = 1 ]; then' \
  'test -d "$release_dir"' \
  'if [ ! -e /etc/miningcore/config.json ]; then' \
  'sudo cp "$release_dir/config.example.json" /etc/miningcore/config.json' \
  'sudo ln -sfnT "$release_dir" /opt/miningcore' \
  'STOP: installation failed; /opt/miningcore was not changed'; do
  if ! grep -Fq "$required" <<<"$install_block"; then
    echo "Release installation block is missing: $required" >&2
    exit 1
  fi
done

if grep -Fq 'sudo cp /opt/miningcore/config.example.json' <<<"$install_block"; then
  echo "Release installation block reads configuration through the old live symlink" >&2
  exit 1
fi

directory_guard_line=$(grep -nF 'test -d "$release_dir"' <<<"$install_block" | cut -d: -f1)
symlink_line=$(grep -nF 'sudo ln -sfnT "$release_dir" /opt/miningcore' <<<"$install_block" |
  cut -d: -f1)

if [[ -z "$directory_guard_line" || -z "$symlink_line" ||
    "$symlink_line" -le "$directory_guard_line" ]]; then
  echo "The stable symlink must move only after the extracted directory is verified" >&2
  exit 1
fi

blocked_install_output=$(MININGCORE_RELEASE_READY= bash -c "$install_block" 2>&1)
if ! grep -Fq 'no release archive passed' <<<"$blocked_install_output"; then
  echo "The installation block did not stop cleanly without a verified archive" >&2
  exit 1
fi

echo "Release installation documentation invariants passed"
