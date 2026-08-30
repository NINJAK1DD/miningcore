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
assert_contains "root-only credential pre-creation" \
  'sudo install -m 0600 -o root -g root /dev/null '
assert_contains "root-only replacement credential pre-creation" \
  '/etc/miningcore/miningcore.env.new'
assert_contains "service-group directory ownership" \
  'sudo chown root:miningcore /etc/miningcore'
assert_contains "service-readable directory mode" \
  'sudo chmod 0750 /etc/miningcore'
assert_contains "root-only credential ownership" \
  'sudo chown root:root /etc/miningcore/miningcore.env'
assert_contains "root-only credential mode" \
  'sudo chmod 0600 /etc/miningcore/miningcore.env'

if ! awk '
  /^```/ {
    in_code = !in_code
    pending_install = 0
    secure_live = 0
    secure_new = 0
    next
  }
  !in_code { next }
  /sudo install -m 0600 -o root -g root \/dev\/null/ {
    pending_install = 1
    next
  }
  pending_install && /\/etc\/miningcore\/miningcore\.env\.new/ {
    secure_new = 1
    pending_install = 0
    next
  }
  pending_install && /\/etc\/miningcore\/miningcore\.env/ {
    secure_live = 1
    pending_install = 0
    next
  }
  /sudo tee \/etc\/miningcore\/miningcore\.env\.new/ && !secure_new {
    unsafe = 1
  }
  /sudo tee \/etc\/miningcore\/miningcore\.env([^.]|$)/ && !secure_live {
    unsafe = 1
  }
  END { exit unsafe ? 1 : 0 }
' "$security_guide"; then
  echo 'Administrative API guide writes a token before creating its root-only file' >&2
  exit 1
fi
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
assert_prose_contains "fixed-size admin whitelist rejection limiter" \
  'Admin[[:space:]]+IP-whitelist[[:space:]]+rejections[[:space:]]+have[[:space:]]+a[[:space:]]+separate[[:space:]]+fixed-size,[[:space:]]+monotonic[[:space:]]+log[[:space:]]+limiter'
assert_prose_contains "independent metrics whitelist rejection limiter" \
  'Metrics[[:space:]]+whitelist[[:space:]]+logging[[:space:]]+has[[:space:]]+an[[:space:]]+independent[[:space:]]+limiter'
assert_prose_contains "whitelist rejection remains fail-closed" \
  'All[[:space:]]+rejected[[:space:]]+requests[[:space:]]+continue[[:space:]]+to[[:space:]]+return[[:space:]]+`403[[:space:]]+Forbidden`'
assert_prose_contains "debug rejection logging volume warning" \
  'enabling[[:space:]]+that[[:space:]]+level[[:space:]]+during[[:space:]]+hostile[[:space:]]+traffic[[:space:]]+can[[:space:]]+substantially[[:space:]]+increase[[:space:]]+log[[:space:]]+volume'
assert_prose_contains "suppression summaries require a later rejection" \
  'A[[:space:]]+summary[[:space:]]+is[[:space:]]+emitted[[:space:]]+only[[:space:]]+when[[:space:]]+another[[:space:]]+rejection[[:space:]]+arrives[[:space:]]+after[[:space:]]+the[[:space:]]+interval'
assert_prose_contains "whitelist rejection counter" \
  '`miningcore_api_ip_whitelist_rejections_total`[[:space:]]+increments[[:space:]]+for[[:space:]]+every[[:space:]]+source-IP[[:space:]]+whitelist[[:space:]]+rejection'
assert_prose_contains "fixed whitelist metric cardinality" \
  'fixed[[:space:]]+values[[:space:]]+`admin`,[[:space:]]+`metrics`[[:space:]]+or[[:space:]]+`other`;[[:space:]]+it[[:space:]]+never[[:space:]]+includes[[:space:]]+a[[:space:]]+client[[:space:]]+address[[:space:]]+or[[:space:]]+request[[:space:]]+path'
assert_prose_contains "atomic payment-processing toggles" \
  'Payment-processing[[:space:]]+toggles[[:space:]]+are[[:space:]]+validated[[:space:]]+atomically[[:space:]]+before[[:space:]]+any[[:space:]]+pool[[:space:]]+is[[:space:]]+changed'
assert_prose_contains "active PPS disable prohibition" \
  'rejects[[:space:]]+disabling[[:space:]]+payment[[:space:]]+processing[[:space:]]+while[[:space:]]+an[[:space:]]+enabled[[:space:]]+PPS[[:space:]]+pool[[:space:]]+is[[:space:]]+accepting[[:space:]]+shares'
assert_prose_contains "PPS controlled-restart requirement" \
  'make[[:space:]]+PPS[[:space:]]+contract[[:space:]]+changes[[:space:]]+through[[:space:]]+a[[:space:]]+reviewed[[:space:]]+configuration[[:space:]]+and[[:space:]]+controlled[[:space:]]+restart'
assert_prose_contains "non-PPS per-pool toggle fallback" \
  'If[[:space:]]+a[[:space:]]+bulk[[:space:]]+disable[[:space:]]+is[[:space:]]+rejected,[[:space:]]+non-PPS[[:space:]]+pools[[:space:]]+remain[[:space:]]+individually[[:space:]]+controllable[[:space:]]+through[[:space:]]+their[[:space:]]+per-pool[[:space:]]+routes'

echo "Administrative API documentation invariants are present"
