#!/usr/bin/env bash
set -euo pipefail

: "${PGHOST:?set PGHOST}"
: "${PGPORT:?set PGPORT}"
: "${PGUSER:?set PGUSER}"
: "${PGDATABASE:?set PGDATABASE}"
: "${PGPASSWORD:?set PGPASSWORD}"

relay_host="${1:?usage: $0 RELAY_HOST RELAY_PORT POOL_ID SENDER_SOURCE [TIMEOUT_SECONDS]}"
relay_port="${2:?missing relay port}"
pool_id="${3:?missing pool id}"
sender_source="${4:?missing sender cluster/source name}"
timeout_seconds="${5:-120}"

if ! timeout 5 bash -c "</dev/tcp/${relay_host}/${relay_port}" 2>/dev/null; then
    echo "FAIL: cannot reach relay ${relay_host}:${relay_port} from $(hostname)" >&2
    exit 1
fi

started_at="$(psql -Atqc 'SELECT clock_timestamp()')"
echo "Relay TCP path is reachable from $(hostname)."
echo "Waiting up to ${timeout_seconds}s for a new ordinary share from source '${sender_source}' in pool '${pool_id}'."
echo "Submit mining work through the physical sender now."

deadline=$((SECONDS + timeout_seconds))
while (( SECONDS < deadline )); do
    count="$(psql -At -v pool="$pool_id" -v source="$sender_source" \
        -v started="$started_at" <<'SQL'
SELECT count(*)
FROM shares
WHERE poolid = :'pool'
  AND source = :'source'
  AND created >= :'started'::timestamptz;
SQL
)"

    if (( count > 0 )); then
        echo "PASS: ${count} new relayed share(s) reached PostgreSQL through the physical topology."
        echo "Merged block candidates do not depend on this PUB/SUB path; submitting nodes persist them synchronously."
        exit 0
    fi

    sleep 2
done

echo "FAIL: no qualifying share reached PostgreSQL before timeout." >&2
echo "Inspect sender logs, receiver logs, ZeroMQ binding/Curve keys, firewall/NAT and the configured source name." >&2
exit 1
