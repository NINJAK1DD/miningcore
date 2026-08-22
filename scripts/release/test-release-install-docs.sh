#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
document="$repository_root/docs/releases.md"
capability_dir=

cleanup() {
  if [[ -n "$capability_dir" ]]; then
    rm -f -- "$capability_dir/link"
    rmdir -- "$capability_dir/target" "$capability_dir" 2>/dev/null || true
  fi
}
trap cleanup EXIT

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
verification_block=$(awk '
  { sub(/\r$/, "") }
  /^Compare the extracted metadata with the binary/ { section = 1; next }
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
assert_contains 'the staged-publication state model' \
  'No publication | No release and no version-scoped staging tag'
assert_contains 'the durable-release promotion boundary' \
  'published GitHub Release permits the public version tags'
assert_contains 'the publication conflict stop' \
  'HUMAN ACTION REQUIRED'
assert_contains 'the non-transactional publication boundary' \
  'do not provide a shared transaction'
assert_contains 'the authenticated draft-discovery command' \
  'gh api --paginate --slurp'
assert_contains 'the exact-one release selection guard' \
  'expected exactly one matching draft or published release'
assert_contains 'the bounded release-list visibility policy' \
  'retries those visibility checks for a bounded period'
assert_contains 'the repository-wide publication queue' \
  'queues up to 100 release tags'
assert_contains 'the publication queue timeout' \
  '60-minute job'
assert_contains 'the draft ownership boundary' \
  'hidden ownership marker bound to the repository'
assert_contains 'the streamed bounded upload policy' \
  'uploads are streamed'
assert_contains 'the pruned-stage recovery rule' \
  'at least one immutable GHCR version tag still matches the recorded'
assert_contains 'the GitHub CLI publication floor' \
  'Publication requires GitHub CLI'
assert_contains 'the orphan-stage recovery warning' \
  'Never delete a staging tag merely to bypass a digest or'
assert_contains 'the declared build-image contract' \
  'workflow-declared'
assert_contains 'the Ubuntu 22.04 curl compatibility statement' \
  'curl version supplied by Ubuntu 22.04'
assert_contains 'the path-filtered branch-protection warning' \
  'Do not configure it as a required'

recovery_section=$(awk '
  /^### Recover an interrupted publication$/ { capture = 1 }
  capture { print }
' "$document")
if grep -Fq 'releases/tags/$TAG' <<<"$recovery_section"; then
  echo 'Release recovery documentation still uses the published-only tag endpoint' >&2
  exit 1
fi

if grep -Eq '(^|[[:space:]])exit([[:space:]]|$)' <<<"$selection_block"; then
  echo "The copy-paste release selection block must not exit an interactive shell" >&2
  exit 1
fi

bash -n <<<"$selection_block"
bash -n <<<"$install_block"
bash -n <<<"$verification_block"

for required in \
  'MININGCORE_HOST_RELEASE=' \
  'MININGCORE_RELEASE_READY=' \
  'MININGCORE_INSTALL_READY=' \
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

sha256sum_help=$(sha256sum --help)
for checksum_option in --ignore-missing --strict; do
  if ! grep -Fq -- "$checksum_option" <<<"$sha256sum_help"; then
    echo "The current sha256sum does not support documented option $checksum_option" >&2
    exit 1
  fi
done

if ! grep -Fq -- '--no-target-directory' <<<"$(ln --help)"; then
  echo 'The current ln does not support documented option -T' >&2
  exit 1
fi

capability_dir=$(mktemp -d "${TMPDIR:-/tmp}/miningcore-doc-test.XXXXXXXX")
mkdir "$capability_dir/target"
ln -sfnT "$capability_dir/target" "$capability_dir/link"
test -L "$capability_dir/link"
rm -f -- "$capability_dir/link"
rmdir -- "$capability_dir/target" "$capability_dir"
capability_dir=

for required in \
  'MININGCORE_INSTALL_READY=' \
  'if [ "${MININGCORE_RELEASE_READY:-}" = 1 ]; then' \
  'test -d "$release_dir"' \
  'if [ ! -e /etc/miningcore/config.json ]; then' \
  'sudo cp "$release_dir/config.example.json" /etc/miningcore/config.json' \
  'sudo ln -sfnT "$release_dir" /opt/miningcore' \
  'MININGCORE_RELEASE_READY=' \
  'rmdir -- "$MININGCORE_DOWNLOAD_DIR"' \
  'WARN: remove the verified release files from $MININGCORE_DOWNLOAD_DIR' \
  'export MININGCORE_INSTALL_READY=1' \
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

find_unique_line() {
  local label=$1
  local pattern=$2
  local source=$3
  local -a matches

  mapfile -t matches < <(grep -nF "$pattern" <<<"$source" | cut -d: -f1)
  if [[ "${#matches[@]}" -ne 1 ]]; then
    echo "Installation-order anchor '$label' occurred ${#matches[@]} times; expected 1" >&2
    return 1
  fi

  printf '%s\n' "${matches[0]}"
}

# Keep these as bare assignments: set -e makes an ambiguous or missing anchor fail the test.
directory_guard_line=$(find_unique_line directory-guard 'test -d "$release_dir"' "$install_block")
symlink_line=$(
  find_unique_line stable-symlink \
    'sudo ln -sfnT "$release_dir" /opt/miningcore' "$install_block"
)
release_consumed_line=$(
  find_unique_line release-consumed 'MININGCORE_RELEASE_READY=' "$install_block"
)
cleanup_line=$(
  find_unique_line download-cleanup 'rmdir -- "$MININGCORE_DOWNLOAD_DIR"' "$install_block"
)

duplicate_anchor_block="${install_block}"$'\nMININGCORE_RELEASE_READY='
if find_unique_line duplicate-guard 'MININGCORE_RELEASE_READY=' \
    "$duplicate_anchor_block" >/dev/null 2>&1; then
  echo 'The installation-order anchor guard accepted an ambiguous duplicate' >&2
  exit 1
fi

if [[ "$symlink_line" -le "$directory_guard_line" ||
    "$release_consumed_line" -le "$symlink_line" ||
    "$cleanup_line" -le "$release_consumed_line" ]]; then
  echo 'The stable symlink, readiness reset and cleanup operations are not safely ordered' >&2
  exit 1
fi

blocked_install_output=$(
  env -u MININGCORE_RELEASE_READY -u MININGCORE_INSTALL_READY \
    bash -c "$install_block" 2>&1
)
if ! grep -Fq 'no release archive passed' <<<"$blocked_install_output"; then
  echo "The installation block did not stop cleanly without a verified archive" >&2
  exit 1
fi

for required in \
  'if [ "${MININGCORE_INSTALL_READY:-}" = 1 ]' \
  '[ -n "${release_dir:-}" ] && [ -d "$release_dir" ]' \
  'cat "$release_dir/BUILD-INFO"' \
  'STOP: no release from this installation run is available to verify'; do
  if ! grep -Fq "$required" <<<"$verification_block"; then
    echo "Release verification block is missing: $required" >&2
    exit 1
  fi
done

blocked_verification_output=$(
  env -u MININGCORE_INSTALL_READY -u release_dir bash -c "$verification_block" 2>&1
)
if ! grep -Fq 'no release from this installation run' <<<"$blocked_verification_output"; then
  echo 'The verification block did not reject stale or absent installation state' >&2
  exit 1
fi

echo "Release installation documentation invariants passed"
