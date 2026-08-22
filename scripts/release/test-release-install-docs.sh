#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
document="$repository_root/docs/releases.md"
selection_block=$(awk '
  /^export MININGCORE_VERSION=/ { capture = 1 }
  capture { print }
  capture && /^```$/ { exit }
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
assert_contains 'the pinned build-image evidence' \
  'reference is recorded in each archive'

if grep -Eq '(^|[[:space:]])exit([[:space:]]|$)' <<<"$selection_block"; then
  echo "The copy-paste release selection block must not exit an interactive shell" >&2
  exit 1
fi

for required in \
  'MININGCORE_HOST_RELEASE=' \
  'MININGCORE_RELEASE_READY=' \
  'if [ -n "$MININGCORE_UBUNTU" ]; then' \
  'if curl --fail --location --remote-name --remove-on-error' \
  'sha256sum --ignore-missing --check --strict SHA256SUMS; then' \
  'export MININGCORE_RELEASE_READY=1' \
  'rm -f -- "$archive" SHA256SUMS' \
  'archive='; do
  if ! grep -Fq "$required" <<<"$selection_block"; then
    echo "Release selection block is missing: $required" >&2
    exit 1
  fi
done

echo "Release installation documentation invariants passed"
