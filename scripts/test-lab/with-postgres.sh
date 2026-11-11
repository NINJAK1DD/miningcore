#!/usr/bin/env bash
set -euo pipefail
umask 077
if [[ $(id -u) == 0 || $# == 0 ]]; then
  echo 'Usage (ordinary lab user): bash with-postgres.sh COMMAND [ARGS...]' >&2
  exit 64
fi
pg_bins=(/usr/lib/postgresql/*/bin/initdb)
pg_bin=${MININGCORE_TEST_POSTGRES_BIN:-$(dirname "${pg_bins[-1]}")}
test -x "$pg_bin/initdb"
scratch=$(mktemp -d /tmp/miningcore-lab-postgres.XXXXXXXX)
cleanup() {
  "$pg_bin/pg_ctl" -D "$scratch/data" -m fast -w stop >/dev/null 2>&1 || true
  case "$scratch" in /tmp/miningcore-lab-postgres.*) rm -rf -- "$scratch" ;; esac
}
trap cleanup EXIT
port=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
password=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
printf '%s\n' "$password" >"$scratch/password"
"$pg_bin/initdb" -D "$scratch/data" -U miningcore -A scram-sha-256 \
  --pwfile="$scratch/password" --no-locale >"$scratch/initdb.log"
"$pg_bin/pg_ctl" -D "$scratch/data" -l "$scratch/postgres.log" \
  -o "-h 127.0.0.1 -p $port -k $scratch" -w start >/dev/null
PGPASSWORD="$password" "$pg_bin/createdb" -h 127.0.0.1 -p "$port" -U miningcore miningcore_tests
# Current accounting fixtures create/drop roles and test separate administrator ownership.
# The administrator exists only in this temporary cluster, never in the shared lab database.
export MININGCORE_TEST_POSTGRES="Host=127.0.0.1;Port=$port;Username=miningcore;Password=$password;Database=miningcore_tests"
export MININGCORE_TEST_POSTGRES_BIN="$pg_bin"
"$@"
