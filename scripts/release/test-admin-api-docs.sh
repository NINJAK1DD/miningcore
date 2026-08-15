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

assert_prose_contains() {
  local description=$1
  local pattern=$2

  # Treat the document as one record so ordinary Markdown reflow does not
  # invalidate security invariants merely by moving words across lines.
  if ! grep -Ezq "$pattern" "$security_guide"; then
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
assert_prose_contains "Docker recreation warning" \
  '`docker[[:space:]]+restart`[[:space:]]+is[[:space:]]+insufficient'
assert_prose_contains "tombstone-route prohibition" \
  'No[[:space:]]+unauthenticated[[:space:]]+`410[[:space:]]+Gone`[[:space:]]+tombstone[[:space:]]+is[[:space:]]+registered[[:space:]]+by[[:space:]]+design'
assert_prose_contains "protected response resource policy" \
  'Every[[:space:]]+administrative[[:space:]]+response[[:space:]]+produced[[:space:]]+by[[:space:]]+the[[:space:]]+API[[:space:]]+pipeline[[:space:]]+sends[[:space:]]+`Cross-Origin-Resource-Policy:[[:space:]]+same-origin`'
assert_prose_contains "protected responses are non-cacheable" \
  '`Cache-Control:[[:space:]]+no-store`'
assert_prose_contains "protected responses disable MIME sniffing" \
  '`X-Content-Type-Options:[[:space:]]+nosniff`'
assert_prose_contains "protocol-layer response limitation" \
  'protocol[[:space:]]+errors[[:space:]]+that[[:space:]]+Kestrel[[:space:]]+rejects[[:space:]]+before[[:space:]]+a[[:space:]]+request[[:space:]]+enters[[:space:]]+the[[:space:]]+API[[:space:]]+pipeline'
assert_prose_contains "resource policy is not authentication" \
  'never[[:space:]]+replace[s]?[[:space:]]+the[[:space:]]+dedicated[[:space:]]+listener,[[:space:]]+IP[[:space:]]+whitelist,[[:space:]]+bearer[[:space:]]+token,[[:space:]]+TLS[[:space:]]+or[[:space:]]+firewall[[:space:]]+boundary'
assert_prose_contains "resource policy is not a framing control" \
  'does[[:space:]]+not[[:space:]]+generally[[:space:]]+prohibit[[:space:]]+cross-origin[[:space:]]+navigation[[:space:]]+or[[:space:]]+iframe[[:space:]]+embedding'

echo "Administrative API documentation invariants are present"
