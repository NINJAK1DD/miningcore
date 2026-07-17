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
sudo -u postgres pg_dump -Fc -d miningcore > miningcore-$(date +%F).dump
pg_restore --list miningcore-$(date +%F).dump > /dev/null
```

The local administrator form includes partitions and other objects created by an administrative
role even when the runtime `miningcore` role cannot lock those objects directly. For a remote
database, use a dedicated backup role with read and lock access to every schema object instead of a
superuser.

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

The ownership migration assigns its three new tables to the owner of the current database. Confirm
that this is the same role configured under `persistence.postgres.user` and inspect the resulting
owners before restarting Miningcore:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore -c "
SELECT current_database() AS database,
       pg_get_userbyid(datdba) AS database_owner
FROM pg_database
WHERE datname = current_database();

SELECT schemaname, tablename, tableowner
FROM pg_tables
WHERE tablename IN (
  'share_recovery_imports',
  'payment_batches',
  'payout_manager_ownership'
)
ORDER BY schemaname, tablename;"
```

If the database owner is not the configured application role, correct the database ownership or
grant that application role the required table privileges before startup. Do not solve this by
making the Miningcore runtime role a PostgreSQL superuser.

The payout ownership migration is required wherever payment processing is enabled and for recorder or
recovery-only deployments using the `-rs` importer. The AuxPoW migration is required before enabling
LTC/DOGE merged mining. Both scripts stop instead of guessing when legacy duplicates require manual
review. Recovery mode validates the `share_recovery_imports` table, its required columns and its
immediate `filehash` primary key before scanning the journal, so a missing or stale migration fails
early with an actionable message.

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
converts `shares` to a list-partitioned layout. This can improve a large multipool cluster because
most Miningcore queries are scoped to one pool. It is not needed for a first installation or a
small pool.

> [!CAUTION]
> The appendix deletes and rebuilds `shares`. Stop every recorder and recovery importer first.
> Take a verified, restorable backup and practise the complete procedure on a restored database.
> Do not run it against a live production database with active writers.

### 1. Back up the shares and record the baseline

Create both the normal full backup described above and a focused shares archive:

```console
sudo -u postgres pg_dump -Fc -d miningcore -t public.shares \
  > shares-before-partition.dump
pg_restore --list shares-before-partition.dump > /dev/null
```

Record the count for every pool so the restored result can be compared:

```console
sudo -u postgres psql -d miningcore -c "
SELECT poolid, count(*)
FROM public.shares
GROUP BY poolid
ORDER BY poolid;"
```

### 2. Convert the parent table

From the repository root, with Miningcore stopped:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/createdb_postgresql_11_appendix.sql
```

The script is transactional, but a successful run intentionally leaves the new parent empty and
without child partitions.

### 3. Create one partition for every pool ID

This step is mandatory. The text in `FOR VALUES IN (...)` must exactly match the pool's `id` in
`config.json`. The child table name is an ordinary PostgreSQL identifier and may replace hyphens
with underscores:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore
```

```sql
SET ROLE miningcore;

CREATE TABLE public.shares_btc1_solo
PARTITION OF public.shares
FOR VALUES IN ('btc1-solo');

CREATE TABLE public.shares_ltc1_solo
PARTITION OF public.shares
FOR VALUES IN ('ltc1-solo');

CREATE TABLE public.shares_doge1_solo
PARTITION OF public.shares
FOR VALUES IN ('doge1-solo');

RESET ROLE;
```

Use your actual enabled pool IDs, not the examples. Create a partition before enabling any new
pool later. An auxiliary DOGE block-only record does not create an ordinary share, but a DOGE pool
that can accept direct miners still needs its own partition.

Miningcore now checks this during startup on direct recorder nodes, share-relay receivers and
recovery imports. If an enabled pool has no matching partition, startup fails before Stratum opens
or recovery data is imported. Sender-only share-relay nodes skip this local check because their
ordinary shares are recorded elsewhere.

A PostgreSQL `DEFAULT` partition is also routable and therefore passes the preflight, but dedicated
per-pool partitions preserve the performance and operational isolation this layout is intended to
provide.

### 4. Restore and verify the shares

Restore the saved rows only after every required partition exists:

```console
sudo -u postgres pg_restore --exit-on-error --data-only \
  --table=public.shares -d miningcore shares-before-partition.dump
```

List the partition bounds and owners:

```console
sudo -u postgres psql -d miningcore -c "
SELECT child.relname AS partition_name,
       pg_get_userbyid(child.relowner) AS owner,
       pg_get_expr(child.relpartbound, child.oid) AS partition_bound
FROM pg_inherits
JOIN pg_class parent ON parent.oid = inhparent
JOIN pg_class child ON child.oid = inhrelid
WHERE parent.oid = 'public.shares'::regclass
ORDER BY child.relname;"
```

Repeat the per-pool count query from step 1 and compare every result before restarting Miningcore.
Keep the backup until normal share persistence and statistics have been observed successfully.
