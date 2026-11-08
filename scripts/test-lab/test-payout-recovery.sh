#!/usr/bin/env bash
# Isolated live PostgreSQL/systemd and terminal checks; never stops the shared lab database.
set -euo pipefail
umask 077
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
test_host="$repo/src/Miningcore.Tests.ProcessHost/bin/Debug/net10.0/Miningcore.Tests.ProcessHost.dll"
app="$repo/src/Miningcore/bin/Debug/net10.0/Miningcore.dll"
test -f "$test_host"
test -f "$app"
sudo -n true
pg_bins=(/usr/lib/postgresql/*/bin/initdb)
pg_bin=$(dirname "${pg_bins[-1]}")
scratch=$(mktemp -d /tmp/miningcore-payout-recovery.XXXXXXXX)
unit="miningcore-payout-test-$(basename "$scratch" | tr -cd 'a-zA-Z0-9')"
pg_unit="$unit-postgres.service"
pool_unit="$unit-pool.service"
restart_unit="$unit-restart.service"
cleanup() {
  sudo systemctl stop "$pool_unit" "$pg_unit" "$restart_unit" >/dev/null 2>&1 || true
  sudo systemctl reset-failed "$pool_unit" "$pg_unit" "$restart_unit" >/dev/null 2>&1 || true
  sudo rm -f -- "/run/systemd/system/$pool_unit" "/run/systemd/system/$pg_unit" "/run/systemd/system/$restart_unit"
  sudo systemctl daemon-reload
  case "$scratch" in /tmp/miningcore-payout-recovery.*) rm -rf -- "$scratch" ;; esac
}
trap cleanup EXIT
export PGHOST=127.0.0.1 PGUSER
if [[ $(id -u) == 0 ]]; then
  PGUSER=${MININGCORE_LAB_USER:-ubuntu}
  chown "$PGUSER" "$scratch"
else
  PGUSER=$(id -un)
fi
export PGPORT
PGPORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
export PGDATABASE=postgres
admin_password=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
app_password=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
printf '%s\n' "$admin_password" >"$scratch/admin-password"
printf '%s:%s:*:%s:%s\n' "$PGHOST" "$PGPORT" "$PGUSER" "$admin_password" >"$scratch/admin.pgpass"
printf '%s:%s:*:miningcore:%s\n' "$PGHOST" "$PGPORT" "$app_password" >"$scratch/app.pgpass"
sudo chown "$PGUSER" "$scratch/admin-password" "$scratch/app.pgpass"
export PGPASSFILE="$scratch/admin.pgpass"
# The caller owns this log; only initdb must run as the unprivileged database user.
# shellcheck disable=SC2024
sudo -u "$PGUSER" "$pg_bin/initdb" -D "$scratch/data" -A scram-sha-256 --pwfile="$scratch/admin-password" --no-locale >"$scratch/initdb.log"
cat >"$scratch/$pg_unit" <<EOF
[Service]
Type=simple
User=$PGUSER
KillSignal=SIGINT
ExecStart=$pg_bin/postgres -D $scratch/data -h 127.0.0.1 -p $PGPORT -k $scratch
EOF
sudo install -m 0644 "$scratch/$pg_unit" "/run/systemd/system/$pg_unit"
sudo systemctl daemon-reload
sudo systemctl start "$pg_unit"
wait_postgres() {
  for ((i=0; i<100; i++)); do
    if "$pg_bin/pg_isready" -q; then return; fi
    sleep 0.1
  done
  return 1
}
wait_postgres
psql -X -v ON_ERROR_STOP=1 >/dev/null <<SQL
CREATE ROLE miningcore LOGIN PASSWORD '$app_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
ALTER DATABASE postgres OWNER TO miningcore;
CREATE TABLE shares(created timestamp without time zone);
ALTER TABLE shares OWNER TO miningcore;
CREATE TABLE payment_batches(poolid text NOT NULL, transactionconfirmationdata text NOT NULL,
  created timestamptz NOT NULL, PRIMARY KEY(poolid, transactionconfirmationdata));
CREATE TABLE payout_manager_ownership(id smallint PRIMARY KEY CHECK(id=1), generation bigint NOT NULL DEFAULT 0,
  owner_id uuid, owner_host text, owner_process_id integer, acquired timestamptz, released timestamptz);
ALTER TABLE payment_batches OWNER TO miningcore;
ALTER TABLE payout_manager_ownership OWNER TO miningcore;
-- CREATE allows the missing-table fixture to rename its own table, not admin-owned audit storage.
GRANT USAGE, CREATE ON SCHEMA public TO miningcore;
SQL
psql -X -v ON_ERROR_STOP=1 -f "$repo/src/Miningcore/Persistence/Postgres/Scripts/add_payout_recovery_audit.sql" >/dev/null

sudo -u "$PGUSER" env -i PYTHONUNBUFFERED=1 PATH="$PATH" HOME="$(getent passwd "$PGUSER" | cut -d: -f6)" \
  PGHOST="$PGHOST" PGPORT="$PGPORT" PGUSER=miningcore PGDATABASE="$PGDATABASE" \
  PGPASSFILE="$scratch/app.pgpass" python3 "$repo/scripts/test-lab/test-payout-recovery-cli.py" "$app" "$scratch" "$test_host"

psql -X -v ON_ERROR_STOP=1 -f "$repo/src/Miningcore/Persistence/Postgres/Scripts/add_payout_recovery_audit.sql" >/dev/null
audit_schema=$(psql -X -Atc "SELECT 'miningcore_audit_' || 'payout_manager_ownership'::regclass::oid::text")
test "$(psql -X -Atc "SELECT count(*) FROM $audit_schema.payout_manager_recovery_events")" -eq 2
echo 'PASS: reapplying the audit migration preserves recorded releases'

printf '%s\n' "MININGCORE_TEST_POSTGRES=Host=127.0.0.1;Port=$PGPORT;Username=miningcore;Password=$app_password;Database=postgres" >"$scratch/pool.env"
# Use the documented actual-unit drop-in dependencies against this isolated database.
cat >"$scratch/$pool_unit" <<EOF
[Unit]
After=$pg_unit
Wants=$pg_unit
[Service]
Type=simple
User=$PGUSER
TimeoutStopSec=90
WorkingDirectory=$repo
EnvironmentFile=$scratch/pool.env
NoNewPrivileges=true
ExecStart=/usr/bin/dotnet $test_host payout-lease - $scratch/ready
EOF
sudo install -m 0644 "$scratch/$pool_unit" "/run/systemd/system/$pool_unit"
sudo systemctl daemon-reload
sudo systemctl start "$pool_unit"
wait_pool() {
  for ((i=0; i<100; i++)); do
    if test -f "$scratch/ready"; then return; fi
    sleep 0.1
  done
  sudo journalctl -u "$pool_unit" --no-pager
  return 1
}
wait_pool
# Independent maintenance follows the documented manual sequence.
sudo systemctl stop "$pool_unit"
sudo systemctl stop "$pg_unit"
test -f "$scratch/ready.released"
sudo systemctl start "$pg_unit"
wait_postgres
test "$(psql -X -Atc 'SELECT owner_id IS NULL FROM payout_manager_ownership')" = t
echo 'PASS: planned maintenance releases ownership before stopping PostgreSQL'

rm -- "$scratch/ready" "$scratch/ready.released"
sudo systemctl start "$pool_unit"
wait_pool
# The same stop transaction used at host shutdown must preserve the reverse order.
sudo systemctl stop "$pool_unit" "$pg_unit"
test -f "$scratch/ready.released"
sudo systemctl start "$pg_unit"
wait_postgres
test "$(psql -X -Atc 'SELECT owner_id IS NULL FROM payout_manager_ownership')" = t
echo 'PASS: simultaneous shutdown stops Miningcore before PostgreSQL'

# The packaged helper supplies the actual cluster dependency, not a blanket umbrella.
grep -q '^After=' "$repo/packaging/systemd/postgresql-ordering.conf.example"
grep -q '^RestartPreventExitStatus=74 78' "$repo/packaging/systemd/miningcore.service"

# Exercise the actual restart policy, including its ordinary-failure positive control.
for status in 78 1; do
  rm -f -- "$scratch/restarts"
  cat >"$scratch/$restart_unit" <<EOF
[Service]
Type=simple
User=$PGUSER
WorkingDirectory=$repo
NoNewPrivileges=true
Restart=on-failure
RestartSec=200ms
RestartPreventExitStatus=74 78
ExecStart=/usr/bin/dotnet $test_host exit-once $scratch/restarts $status
EOF
  sudo install -m 0644 "$scratch/$restart_unit" "/run/systemd/system/$restart_unit"
  sudo systemctl daemon-reload
  sudo systemctl reset-failed "$restart_unit" >/dev/null 2>&1 || true
  sudo systemctl start "$restart_unit"
  for ((i=0; i<100; i++)); do
    state=$(sudo systemctl show "$restart_unit" -p ActiveState --value)
    if [[ $state == failed || $state == inactive ]]; then break; fi
    sleep 0.1
  done
  sleep 1
  count=$(wc -l <"$scratch/restarts")
  if [[ $status == 78 ]]; then
    test "$count" -eq 1
    test "$(sudo systemctl show "$restart_unit" -p ExecMainStatus --value)" -eq 78
    test "$(sudo systemctl show "$restart_unit" -p NRestarts --value)" -eq 0
    echo 'PASS: exit 78 prevents a real systemd restart'
  else
    test "$count" -eq 2
    # NRestarts resets after the second invocation exits successfully. Two recorded
    # starts after one systemctl start prove the automatic retry occurred.
    test "$(sudo systemctl show "$restart_unit" -p ExecMainStatus --value)" -eq 0
    test "$(sudo systemctl show "$restart_unit" -p ActiveState --value)" = inactive
    echo 'PASS: an ordinary failure still restarts under systemd'
  fi
  sudo systemctl stop "$restart_unit"
done
