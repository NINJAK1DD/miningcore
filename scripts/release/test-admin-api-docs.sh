#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
security_guide="$repo_root/docs/admin-api-security.md"

assert_contains() {
  local description=$1
  local text=$2

  if ! grep -Fq "$text" "$security_guide"; then
    echo "Administrative API guide is missing: $description" >&2
    exit 1
  fi
}

if grep -Eq 'install -d.*-g root.*/etc/miningcore' "$security_guide"; then
  echo "Administrative API guide must not make /etc/miningcore root:root mode 0750" >&2
  exit 1
fi

assert_contains "configuration-directory creation" \
  'sudo mkdir -p /etc/miningcore'
assert_contains "service-group directory ownership" \
  'sudo chown root:miningcore /etc/miningcore'
assert_contains "service-readable directory mode" \
  'sudo chmod 0750 /etc/miningcore'
assert_contains "root-only credential ownership" \
  'sudo chown root:root /etc/miningcore/miningcore.env'
assert_contains "root-only credential mode" \
  'sudo chmod 0600 /etc/miningcore/miningcore.env'
assert_contains "Docker recreation warning" \
  '`docker restart` is insufficient'

echo "Administrative API documentation invariants are present"
