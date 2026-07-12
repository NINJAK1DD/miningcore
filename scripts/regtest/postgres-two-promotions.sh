#!/usr/bin/env bash
set -euo pipefail

: "${PGHOST:?set PGHOST}"
: "${PGPORT:?set PGPORT}"
: "${PGUSER:?set PGUSER}"
: "${PGDATABASE:?set PGDATABASE}"
: "${PGPASSWORD:?set PGPASSWORD}"

pool="${1:-regtest-two-promotions}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

cleanup() {
    psql -v ON_ERROR_STOP=1 -c "DELETE FROM blocks WHERE poolid = '$pool'" >/dev/null
}
trap cleanup EXIT

psql -v pool="$pool" -f "$script_dir/postgres-claim-setup.sql"

set +e
psql -v pool="$pool" -f "$script_dir/postgres-claim-promote.sql" > /tmp/promote-a.log 2>&1 &
pid_a=$!
psql -v pool="$pool" -f "$script_dir/postgres-claim-promote.sql" > /tmp/promote-b.log 2>&1 &
pid_b=$!
wait "$pid_a"
exit_a=$?
wait "$pid_b"
exit_b=$?
set -e

cat /tmp/promote-a.log
cat /tmp/promote-b.log
printf 'promotion exits: %s,%s\n' "$exit_a" "$exit_b"

if [[ "$exit_a" -ne 0 || "$exit_b" -ne 0 ]]; then
    echo "A promotion transaction failed" >&2
    exit 1
fi

result="$(psql -Atc "
    SELECT count(*) FILTER (WHERE type = 'auxpow') || ':' ||
           count(*) FILTER (WHERE type = 'auxpow-claim')
    FROM blocks
    WHERE poolid = '$pool' AND hash = 'race-child'
")"
printf 'finalized:claims = %s\n' "$result"

if [[ "$result" != "1:0" ]]; then
    echo "Expected exactly one finalized row and no remaining claim" >&2
    exit 1
fi
