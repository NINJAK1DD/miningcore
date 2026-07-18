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

## Recover payout-manager ownership safely

Miningcore holds both a PostgreSQL session advisory lock and one durable ownership row for the
complete payout-manager lifetime. The advisory lock rejects a second live manager. The durable row
continues to block replacement after a process, host or database-session loss, because an operator
may need to reconcile a wallet submission whose response was lost.

Stop Miningcore before stopping or restarting its PostgreSQL server during planned maintenance. A
clean Miningcore shutdown clears the durable row; stopping PostgreSQL first destroys the guard
session and deliberately leaves the row owned. When PostgreSQL is local, the safe order is:

```console
sudo systemctl stop miningcore
sudo systemctl is-active miningcore
pgrep -af 'Miningcore|Miningcore.dll' || true
sudo systemctl stop postgresql
```

Use the reverse dependency order at startup: PostgreSQL first, then Miningcore. Adapt the service
name when the host runs a versioned PostgreSQL unit, a container or a remote database.

If startup reports a payout-ownership conflict, stop the automatic restart loop before diagnosing
it. Repeated restarts cannot clear the marker:

```console
sudo systemctl stop miningcore
sudo systemctl reset-failed miningcore
pgrep -af 'Miningcore|Miningcore.dll' || true
```

Run the following read-only queries against the exact database and role selected by
`persistence.postgres`. Miningcore does not configure a PostgreSQL schema explicitly; relation
resolution follows the application role's effective `search_path`. Prefer connecting as that role,
then record the resolved schema before inspecting any ownership data:

```console
psql -h REPLACE_WITH_HOST -U REPLACE_WITH_MININGCORE_ROLE \
  -d REPLACE_WITH_DATABASE -x -c "
SELECT current_database(),
       current_user,
       current_schema(),
       current_setting('search_path');

SELECT namespace.nspname AS resolved_schema,
       relation.relname AS resolved_relation
FROM pg_class relation
JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
WHERE relation.oid = to_regclass('payout_manager_ownership');

SELECT id, generation, owner_id, owner_host, owner_process_id,
       acquired, released
FROM REPLACE_WITH_SCHEMA.payout_manager_ownership
WHERE id = 1;"
```

Replace `REPLACE_WITH_SCHEMA` with the `resolved_schema` value that was verified using the
Miningcore application role. Stop if the relation does not resolve or resolves to an unexpected
schema. If an administrative role is required later for the guarded update, keep using this
explicitly inspected schema; do not rely on the administrator's different `search_path`.

The companion advisory lock uses the stable two-key identity `19779, 5259609`. Inspect it without
trying to terminate its PostgreSQL backend:

```console
psql -h REPLACE_WITH_HOST -U REPLACE_WITH_MININGCORE_ROLE \
  -d REPLACE_WITH_DATABASE -x -c "
SELECT activity.pid AS postgres_backend_pid,
       activity.usename,
       activity.application_name,
       activity.client_addr,
       activity.backend_start
FROM pg_locks lock
JOIN pg_stat_activity activity ON activity.pid = lock.pid
WHERE lock.locktype = 'advisory'
  AND lock.database = (
      SELECT oid FROM pg_database WHERE datname = current_database()
  )
  AND lock.classid = 19779::oid
  AND lock.objid = 5259609::oid
  AND lock.objsubid = 2
  AND lock.granted;"
```

- A returned advisory-lock row means a live database session still owns payout processing. Locate
  the recorded host and process, confirm it is the expected Miningcore instance, and stop it
  cleanly. Never clear the durable row underneath it.
- No advisory-lock row plus a populated `owner_id` means the database session is gone but the
  durable marker remains. Confirm the process is absent on the recorded host and on every node
  configured for the same database.
- An empty `owner_id` means this explicitly inspected schema has no durable owner. If startup still
  reports a conflict, verify the database, application role and schema again.

Use the recorded acquisition time and the known stop or database-failure time to define a log
window with a safety margin. A process ID is supporting evidence only: numeric PIDs can be reused,
including across boots, and do not identify a container's host journal records reliably.

For a systemd deployment, select the unit and bounded time window together:

```console
sudo journalctl \
  -u miningcore.service \
  --since 'REPLACE_WITH_ACQUIRED_TIME_MINUS_MARGIN' \
  --until 'REPLACE_WITH_STOP_OR_DATABASE_FAILURE_TIME_PLUS_MARGIN' \
  --no-pager -o short-iso-precise
```

Also constrain the search to the relevant boot (`journalctl --list-boots` and `-b`) or
`_SYSTEMD_INVOCATION_ID` when that identity is available. Add `_PID=...` only after the unit,
boot or invocation, and time boundaries are established; never treat `_PID` alone as exact process
identity.

For a Docker deployment, first identify the exact container and logging driver, then use the same
bounded window:

```console
docker inspect --format '{{.Id}} {{.Name}} {{.HostConfig.LogConfig.Type}}' \
  REPLACE_WITH_CONTAINER
docker logs \
  --since 'REPLACE_WITH_ACQUIRED_TIME_MINUS_MARGIN' \
  --until 'REPLACE_WITH_STOP_OR_DATABASE_FAILURE_TIME_PLUS_MARGIN' \
  REPLACE_WITH_CONTAINER
```

When Docker uses the `journald` logging driver, query the recorded `CONTAINER_ID_FULL` or verified
`CONTAINER_NAME` plus the time window instead. A host PID is not the container identity.

```console
sudo journalctl \
  CONTAINER_ID_FULL=REPLACE_WITH_FULL_CONTAINER_ID \
  --since 'REPLACE_WITH_ACQUIRED_TIME_MINUS_MARGIN' \
  --until 'REPLACE_WITH_STOP_OR_DATABASE_FAILURE_TIME_PLUS_MARGIN' \
  --no-pager -o short-iso-precise
```

Miningcore may write payout-cycle evidence to `logging.logFile` instead of the console. If console
logging was disabled, or a file path was configured, inspect that file and its rotated files over
the same time window. An empty systemd or Docker log is not proof that no wallet submission occurred
until every configured logging destination and the correct service/container invocation have been
checked.

Review every payout cycle after ownership was acquired. If the log only reports `No balances over
configured minimum payout`, no wallet submission began. If it processed payable balances, reported
an unknown wallet outcome, lost transport after submission, or stopped during payment persistence,
reconcile the daemon or wallet transaction history with Miningcore's `payments` and
`payment_batches` records before proceeding. Do not repair balances or payment rows with ad-hoc SQL.

After proving that every previous payout manager is dead and completing any required wallet
reconciliation, release only the owner token and generation that were inspected. Run this block in
`psql` after replacing the explicitly inspected schema, owner token and generation placeholders.
The transaction-level advisory lock prevents a new payout manager from racing the release, and the
expected token/generation prevents an operator from clearing a newer owner accidentally:

```sql
BEGIN;

DO $recovery$
DECLARE
    released_rows integer;
BEGIN
    IF NOT pg_try_advisory_xact_lock(19779, 5259609) THEN
        RAISE EXCEPTION 'A payout manager still holds the advisory lock';
    END IF;

    UPDATE REPLACE_WITH_SCHEMA.payout_manager_ownership
    SET owner_id = NULL,
        owner_host = NULL,
        owner_process_id = NULL,
        released = now()
    WHERE id = 1
      AND generation = REPLACE_WITH_INSPECTED_GENERATION
      AND owner_id = 'REPLACE_WITH_INSPECTED_OWNER_UUID'::uuid;

    GET DIAGNOSTICS released_rows = ROW_COUNT;

    IF released_rows <> 1 THEN
        RAISE EXCEPTION 'The inspected payout-manager owner changed; release aborted';
    END IF;
END
$recovery$;

COMMIT;
```

Start exactly one intended payout manager and confirm that it acquires the next ownership
generation, remains active through a complete payout interval, and starts every expected pool:

```console
sudo systemctl start miningcore
sudo systemctl status miningcore --no-pager
sudo journalctl -u miningcore --since '10 minutes ago' --no-pager
```

If mining and share recording must resume before wallet reconciliation is complete, pause payout
execution without violating the deployment's startup validation:

- On a node without enabled LTC-DOGE merged mining, or on a share-relay sender with the top-level
  `shareRelay` configured, set the top-level `paymentProcessing.enabled` value to `false`.
- On a direct pool or share-relay receiver/recorder with LTC-DOGE merged mining enabled (no
  top-level `shareRelay`), keep the top-level `paymentProcessing.enabled` value `true` and
  temporarily set `paymentProcessing.enabled` to `false` inside **every enabled pool**. Direct and
  receiver nodes require the cluster-level switch for merged-block reconciliation validation, while
  disabling all pool-level switches prevents `PayoutManager` from being registered.

For the merged-mining case, preserve every other payment setting and change only each pool's
`enabled` value:

```json
{
  "paymentProcessing": {
    "enabled": true
  },
  "pools": [
    {
      "id": "ltc",
      "paymentProcessing": {
        "enabled": false
      }
    },
    {
      "id": "doge",
      "paymentProcessing": {
        "enabled": false
      }
    }
  ]
}
```

Validate the complete JSON before starting Miningcore. Either procedure pauses payouts but does not
clear or supersede the durable marker. Restore the original configuration only after ownership
recovery is complete and exactly one designated node is ready to acquire it.

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
