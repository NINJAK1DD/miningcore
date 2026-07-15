# Database setup and upgrades

This guide expands the beginner database steps in the root README. Commands assume PostgreSQL is on
the local Linux host; adjust host names and access controls for a private database server.

## New installation

Use a currently supported PostgreSQL release. PostgreSQL 15 or newer is a sensible baseline for a
new pool even if the schema remains technically compatible with older versions.

```console
sudo -u postgres psql
```

```sql
CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'CHANGE_ME_TO_A_STRONG_PASSWORD';
CREATE DATABASE miningcore OWNER miningcore;
\q
```

Import the complete current schema from the repository root:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/createdb.sql
```

Confirm the application login works:

```console
psql -h 127.0.0.1 -U miningcore -d miningcore -c "SELECT current_user, current_database();"
```

Use `pg_hba.conf`, PostgreSQL TLS and a host firewall when Miningcore and PostgreSQL are on different
machines. Do not expose port 5432 to the internet.

## Back up and restore

Create a compressed logical backup before every schema change:

```console
pg_dump -h 127.0.0.1 -U miningcore -Fc miningcore > miningcore-$(date +%F).dump
pg_restore --list miningcore-$(date +%F).dump > /dev/null
```

Restore into an empty database during a tested recovery exercise:

```console
createdb -h 127.0.0.1 -U postgres -O miningcore miningcore_restore_test
pg_restore -h 127.0.0.1 -U postgres -d miningcore_restore_test --no-owner miningcore-YYYY-MM-DD.dump
```

A backup is not proven until it has been restored and checked.

## Upgrade an existing database

> [!IMPORTANT]
> This revision changes the payout safety contract for every coin family. The payout-manager
> ownership migration is mandatory before starting any node with payment processing enabled, not
> only an LTC/DOGE node. Treat this as a breaking upgrade and schedule a maintenance window.

Stop Miningcore block writers, recovery importers and payout managers, take a verified backup, then
apply the migrations with `ON_ERROR_STOP`:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql

sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/add_payout_manager_ownership.sql
```

The payout ownership migration is required wherever payment processing is enabled and for recorder or
recovery-only deployments using the `-rs` importer. The AuxPoW migration is required before enabling
LTC/DOGE merged mining. Both scripts stop instead of guessing when legacy duplicates require manual
review.

Merged-mining startup verifies its partial unique indexes. Payout processing uses PostgreSQL ownership
and an idempotent payment ledger. Only a clean shutdown clears the durable owner token. After a crash:

1. Prove the previous process is no longer running.
2. Reconcile wallet history for a payout that may have been submitted before its response was lost.
3. Follow the release statement in `add_payout_manager_ownership.sql` only after that reconciliation.
4. Start exactly one payout manager for the pool/database set.

Automatic hot-standby payout takeover is intentionally unsupported.

## Routine inspection

```console
psql -h 127.0.0.1 -U miningcore -d miningcore
```

```sql
\dt
SELECT poolid, blockheight, status, type, created
FROM blocks
ORDER BY created DESC
LIMIT 20;

SELECT poolid, address, amount, created
FROM payments
ORDER BY created DESC
LIMIT 20;
```

Use these queries for inspection only. Never repair balances or payments with ad-hoc SQL.

## Advanced share-table partitioning

The optional
[`createdb_postgresql_11_appendix.sql`](../src/Miningcore/Persistence/Postgres/Scripts/createdb_postgresql_11_appendix.sql)
converts `shares` to a partitioned layout. It deletes and rebuilds that table. Read the complete script,
stop writers, take a verified backup and test the operation on a restored copy before considering it.
It is not needed for a first installation.
