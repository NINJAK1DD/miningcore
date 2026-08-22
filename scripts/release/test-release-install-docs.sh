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

if grep -Eq '(^|[[:space:]])exit([[:space:]]|$)' <<<"$selection_block"; then
  echo "The copy-paste release selection block must not exit an interactive shell" >&2
  exit 1
fi

for required in \
  'MININGCORE_HOST_RELEASE=' \
  'if [ -n "$MININGCORE_UBUNTU" ]; then' \
  'sha256sum --ignore-missing --check --strict SHA256SUMS'; do
  if ! grep -Fq "$required" <<<"$selection_block"; then
    echo "Release selection block is missing: $required" >&2
    exit 1
  fi
done

echo "Release installation documentation invariants passed"
