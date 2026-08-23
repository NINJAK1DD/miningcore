# Database setup and upgrades

Miningcore's database contains financial accounting state. Use the procedure matching the task;
do not improvise SQL from another section during an incident.

| Task | Section |
| --- | --- |
| Create a database | [New installation](#new-installation) |
| Back up or restore | [Back up and restore](#back-up-and-restore) |
| Apply release migrations | [Upgrade an existing database](#upgrade-an-existing-database) |
| Recover after disk exhaustion | [Recover after disk exhaustion](#recover-after-disk-exhaustion) |
| Import a recovery journal | [Inspect and import a recovery journal](#inspect-and-import-a-recovery-journal) |
| Reconcile a fatal recovery latch | [Reconcile fatal share-recovery state](#reconcile-fatal-share-recovery-state) |
| Recover payout ownership | [Recover payout-manager ownership safely](#recover-payout-manager-ownership-safely) |
| Reconcile a wallet submission | [Reconcile a Bitcoin-family payout](#reconcile-a-bitcoin-family-payout) |
| Inspect routine accounting state | [Routine inspection](#routine-inspection) |
| Partition a large shares table | [Advanced share-table partitioning](#advanced-share-table-partitioning) |

Start with [Troubleshooting](troubleshooting.md) when the failing boundary is not yet known.

This guide expands the beginner database steps in the root README. Commands assume PostgreSQL is on
the local Linux host; adjust host names and access controls for a private database server.

| Task | Section |
| --- | --- |
| Create a new database | [New installation](#new-installation) |
| Back up and prove a restore | [Back up and restore](#back-up-and-restore) |
| Upgrade an existing schema | [Upgrade an existing database](#upgrade-an-existing-database) |
| Recover a full filesystem or share journal | [Recover after disk exhaustion](#recover-after-disk-exhaustion) |
| Reconcile a fatal share-accounting incident | [Fatal share-recovery state](#reconcile-fatal-share-recovery-state) |
| Release a stale payout owner safely | [Payout-manager ownership](#recover-payout-manager-ownership-safely) |
| Inspect the live database | [Routine inspection](#routine-inspection) |
| Partition a large shares table | [Advanced partitioning](#advanced-share-table-partitioning) |

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

### Optional next step for larger pools

Before starting Miningcore or importing shares, consider
[Advanced share-table partitioning](#advanced-share-table-partitioning). It is optional, but can
improve query and maintenance isolation for a large multipool deployment. Immediately after initial
schema creation is the simplest time to enable it because the `shares` table is still empty.

Small and single-pool installations can keep the standard table. Partitioning is not a requirement
for correctness, and the appendix must not be run automatically against a populated live database.

### Best-share dashboard data

The current `createdb.sql` already creates the storage and indexes used by the miner dashboard's
`bestShare` and `bestSessionShare` API fields. No separate best-share table or running aggregate is
required:

- `shares.actualdifficulty` stores the achieved difficulty used for the best-share comparison.
- `shares.sessionid` associates a share with the worker's logical mining session.
- `minerstats.sessionid` identifies the current sessions represented by the latest statistics sample.

Miningcore calculates lifetime Best Share as the maximum `actualdifficulty` for the miner. Best
Session Share is the maximum for the session IDs in the miner's current statistics sample. The same
values are calculated per worker. Because they are derived from retained share history, deleting or
archiving old share rows can reduce the reported lifetime Best Share.

Existing databases created from an older schema should verify these columns before deploying the
current binary. Do not rerun `createdb.sql` over an existing database.

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
> The current release series changes the payout safety contract for every coin family. The payout-manager
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
3. Follow the [guarded ownership recovery procedure](#recover-payout-manager-ownership-safely) only
   after that reconciliation.
4. Start exactly one payout manager for the pool/database set.

Automatic hot-standby payout takeover is intentionally unsupported.

## Recover after disk exhaustion

A full filesystem can stop PostgreSQL and coin daemons before Miningcore itself fails. Miningcore
may then show PostgreSQL connection refusals, daemon RPC errors and systemd restart throttling. Treat
those as downstream symptoms: restore storage and dependencies before restarting the pool.

First stop the restart loop and prove which filesystem is full. Check inodes as well as bytes, and
inspect the filesystems that actually contain PostgreSQL data, daemon data and Miningcore logs rather
than assuming they all reside under `/home`:

```console
sudo systemctl stop miningcore
sudo systemctl reset-failed miningcore
pgrep -af 'Miningcore|Miningcore.dll' || true

df -hT
df -i
findmnt -T /var/lib/postgresql
sudo du -xhd1 /var /home 2>/dev/null | sort -h
sudo journalctl --disk-usage
sudo lsof +L1
```

Free space only by removing or moving data whose purpose and recovery consequences are understood.
Do not delete `postmaster.pid`, PostgreSQL WAL files or anything under `/var/lib/postgresql` merely
to reclaim space. An open deleted file shown by `lsof +L1` continues consuming space until its owning
process closes it; investigate that service rather than deleting unrelated database files. Confirm
the affected filesystem now has both free blocks and free inodes before continuing.

Recover PostgreSQL before Miningcore. Ubuntu installations can expose both the umbrella service and
a versioned cluster, so inspect the actual cluster name before choosing a start command:

```console
pg_lsclusters
sudo systemctl status postgresql --no-pager -l
sudo systemctl start postgresql

# If the cluster remains down, substitute the values shown by pg_lsclusters.
sudo pg_ctlcluster REPLACE_WITH_VERSION REPLACE_WITH_CLUSTER start

pg_isready -h 127.0.0.1 -p 5432
psql -h 127.0.0.1 -U REPLACE_WITH_MININGCORE_ROLE \
  -d REPLACE_WITH_DATABASE -c "SELECT now();"
```

If PostgreSQL still fails, read the service and kernel evidence before changing its data directory:

```console
sudo journalctl -u postgresql -u 'postgresql@*' -b --no-pager -n 200
findmnt -no TARGET,SOURCE,FSTYPE,OPTIONS -T /var/lib/postgresql
sudo journalctl -k -b --no-pager -n 200
```

Stop and investigate filesystem or hardware errors when the mount is read-only or the kernel reports
I/O faults. Restore from a verified backup if PostgreSQL reports unrecoverable corruption; do not
attempt speculative WAL or control-file deletion.

Next recover every daemon required by the enabled pools and verify its RPC readiness. A daemon can
accept a process start while still returning an initial-sync or loading-state RPC error, so wait for
its normal blockchain-information call to succeed before starting Miningcore. Use the service names,
CLI clients, RPC ports and authentication from the deployment rather than copying a coin-specific
example blindly.

### Inspect and import a recovery journal

If the incident produced `Share Recorder Policy Fallback`, `Fatal share-recovery fallback failure`
or recovery-journal errors, reconcile the journal before restarting Miningcore. Use the absolute
journal path reported when the Share Recorder came online.

A normal fallback email proves only that its batch was force-flushed. It does not prove every earlier
batch succeeded or reveal shares that reached neither durable target. Review the complete outage
window for successful and fatal fallback messages.

A dual-target failure exits with status 74 and creates a hashed fatal latch below the service state
directory. The fatal log and alert give its exact path. The supplied systemd unit does not restart
status 74, and normal startup remains blocked while the latch exists or cannot be read safely.

#### 1. Stop and preserve the source

Stop Miningcore before inspecting or importing an active journal. Record its metadata and checksum,
then copy the original as incident evidence:

```console
sudo systemctl stop miningcore
sudo stat -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE
sudo sha256sum -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE
sudo cp --preserve=all -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE \
  REPLACE_WITH_EVIDENCE_COPY
```

#### 2. Review damaged records

Do not edit, truncate or import a corrupt original in place. Treat any of the following as possible
partial-write or replay evidence:

- a missing final newline;
- invalid JSON;
- a sequence, previous-frame link, count or hash mismatch; or
- a nested, unclosed or otherwise malformed frame.

Older unframed journals prove only their final newline boundary, not that the last multi-record batch
was complete. Retain the original and create a separate input containing only complete,
operator-reviewed records. Do not guess missing fields or add braces merely to make a fragment parse.

Ordinary share loss can affect proportional and PPLNS reward accounting. Do not dismiss an incomplete
record merely because it is not a block candidate.

#### 3. Run the one-shot importer

Use the reviewed source only after PostgreSQL is healthy:

```console
cd REPLACE_WITH_MININGCORE_INSTALL_DIRECTORY
./Miningcore -c /etc/miningcore/config.json \
  -rs REPLACE_WITH_REVIEWED_RECOVERY_FILE
```

The one-shot importer validates only configuration it consumes. At cluster level that allowlist is
`logging`, `persistence`, `pools`, `shareRecoveryFile`, `shareRecoveryStateDirectory` and optional
`coinTemplates`; malformed or duplicate live-only top-level settings are discarded while the file
is streamed. Recovery retains only the consumed `logging.level` and
`logging.enableConsoleColors` settings and validates their types; stale file-only logging settings
are discarded. An absent, null or wholly malformed logging section is replaced with default
informational, non-coloured console logging so emergency progress remains visible. Recovery returns
after its ownership and PostgreSQL preflights and does not initialize mining, hashing or native
solver runtimes.

Keep a non-empty `pools` array with unique, non-empty pool IDs and configure a complete
`persistence.postgres` endpoint for the target database. Pools may all remain disabled during the
import. Prefer the minimal set of pool IDs actually present in the reviewed journal: partition
preflight checks every configured ID, including an extra historical pool that has no record in this
source. Configured pool IDs are an explicit import allowlist: every journal record must match one
exactly. An unknown or mistyped record ID fails before a pending marker, transaction or manifest is
created. Add an intentional historical pool ID to the recovery configuration only after inspecting
the retained journal.

After a committed import, crash-resume retirement revalidates the marker,
manifest, record count and content hash without requiring those historical IDs to remain in the
current configuration; it cannot replay the already-committed data. Committed cleanup likewise
does not require current AuxPoW indexes because it never replays a block. Fresh or unproven AuxPoW
imports still require those indexes before Miningcore publishes a pending marker or opens the
import transaction.

The configured recovery path and state directory still identify active journal
ownership, terminal anchors and interrupted retirement markers, even when `-rs` names a reviewed
copy.

Live mining settings do not need to be repaired before an emergency import. After ambiguity checks,
recovery rebuilds every pool object from its required `id` and optional string `coin` metadata. All
other pool fields are discarded, including cluster instance identity, enabled state, wallet and
daemon settings, API/Stratum listeners, payout and banning policy, reward recipients, timing values
and extension data. Normal startup restores strict validation for live configuration, so correct it
before restarting the pool.

Recovery checks a partition for every configured pool ID even though all sanitized pools are
disabled. Once the complete journal has passed integrity and semantic validation, Miningcore also
requires `add_auxpow_block_idempotency.sql` when an unpersisted block candidate uses `auxpow`,
`auxpow-claim`, `merged-parent`, or `merged-parent-uncertain`. That requirement comes from the
recovery evidence itself rather than discarded live merged-mining settings, and it is checked before
the import transaction begins.

Coin definitions are notification enrichment, not an import prerequisite. Recovery retains valid
custom template paths and attempts to load and assign templates to every configured pool, including
disabled pools. A missing or malformed path, missing coin metadata, or undefined coin logs a warning
and import continues; a recovered block-found notification may be skipped when no template could be
assigned. Database persistence and journal integrity failures remain fatal.

#### What the importer verifies

Before opening its PostgreSQL transaction, the importer validates:

- every versioned frame's markers, sequence and previous-frame link;
- the declared record count, record SHA-256 and deterministic frame digest;
- the complete terminal anchor when importing the configured chained-v2 recovery journal;
- the source's stable regular-file identity; and
- every record's pool ID against the configured recovery pool allowlist.

Frame hashes normalise line endings to `\n`, so they protect logical records rather than the original
physical newline encoding. Verified frame-comment lines are skipped while records are deserialized.

The importer then commits the records and their `share_recovery_imports` manifest atomically. Its
durable source-retirement marker advances through these phases:

1. `Committed`
2. `ArchiveDurable`
3. `AnchorRetirementAuthorised`
4. `AnchorRetired`

The source is atomically renamed with an `.imported-*` suffix and its directory is synchronized.
The terminal anchor is retired only after durable authorization, and the marker is removed and
synchronized last. Normal startup and fallback appends remain blocked while that marker exists.

#### Resume an interrupted retirement

If recovery reports an archive, directory-sync or anchor-retirement failure:

1. Do not edit, replace or grow the source.
2. Rerun the same recovery command with the same configuration and exact source path.
3. Retain the source, marker and terminal anchor if Miningcore reports changed or unavailable proof.

Before a committed marker can retire the source or anchor, Miningcore uses a fresh PostgreSQL
connection to confirm the exact manifest hash, filename and record count. It then revalidates the
complete chain, terminal anchor, semantic hash, record count and file identity. This allows retirement
to resume without inserting committed records again.

#### Path and overlap safety

- Symlink and hard-link aliases of the configured journal are rejected.
- A stable parent-directory symlink is supported, but later retargeting fails closed.
- Final-component symlinks, FIFOs and other non-regular sources are rejected before database access.
- Retain the imported archive and confirm its manifest row and record count.
- Reconcile the origin and existing manifest of journals from old working directories or deployments.

Manifests identify complete semantic files, not individual shares. Importing `A` and later importing
an overlapping `A+B` source can duplicate `A`; never combine or import overlapping recovery sets.

### Reconcile fatal share-recovery state

An `Uncertain PostgreSQL share commit` incident is deliberately different from an ordinary recovery
journal. Miningcore cannot prove whether PostgreSQL committed, so it does **not** append those shares
to the importable journal.

The status-74 latch names an exact-share sidecar and its expected SHA-256. The sidecar contains one
`shareJsonBase64=` record per line for read-only reconciliation. Verify its hash, decode the records
and compare them with PostgreSQL. Do not copy them into a journal or replay them unless reconciliation
proves they are absent.

A `detailState=hash-pending` latch remains authoritative even if its sidecar is absent or partial.
Earlier v2 incidents may call the equivalent state `incomplete`. Preserve the fixed latch, every
immutable `.incident` file, every sidecar and temporary evidence until each record has a conclusive
disposition.

#### 1. Verify the evidence read-only

Use the same configuration as the service so Miningcore resolves the correct journal and state
directory:

```console
cd REPLACE_WITH_MININGCORE_INSTALL_DIRECTORY
./Miningcore -c /etc/miningcore/config.json --verify-share-recovery-state
echo "exit=$?"
```

The verifier checks:

- every path-scoped incident's metadata, sequence and previous-digest chain;
- the fixed latch's expected incident count and chain tip;
- each sidecar's SHA-256 and decoded record count; and
- stable file identity and length while the restrictive no-follow handle remains open.

Sidecar records are limited to 1,048,576 characters. The smaller `.fatal`, `.incident` and
`.acknowledged` files use strict UTF-8, a cumulative 64-KiB raw-byte limit and a 16-KiB per-line limit.
Concurrent modification, path replacement, malformed data and memory exhaustion all fail
verification instead of being ignored.

The command returns status 74 when evidence is missing, malformed, hash-pending or incomplete. A
completed verified sidecar whose latch still records the immediately preceding hash-pending state is
the one recoverable transition; startup or acknowledgement completes that transition under the
mutation lock but still requires operator reconciliation.

> [!IMPORTANT]
> Successful verification proves only that the evidence is structurally complete. It does not prove
> the uncertain shares were reconciled against PostgreSQL. The verifier never edits, imports or
> deletes recovery files.

#### 2. Preserve the journal frame chain and terminal anchor

Recovery-journal frame chains detect duplicated, missing and reordered middle frames. Fatal
incidents use the separate sequence and previous-digest chain verified in the preceding step. New
journal writes also store their final sequence and digest under
`shareRecoveryStateDirectory/share-recovery-terminal`. Preserve this anchor with the journal during
inspection, backup and restore; startup and import reject a shorter otherwise-valid prefix.

Legacy/v1 history cannot gain retroactive protection. Retain the checksum captured before repair or
import and compare it with backups or monitoring when earlier truncation is plausible. Miningcore
must successfully enumerate the exact state directory before treating a marker as absent. A
directory, symlink, malformed or inaccessible marker remains unreconciled and blocks startup with
status 74.

#### 3. Remove a corrupt journal from the live path

If the configured active journal is corrupt and a separate reviewed copy was imported, preserve the
original evidence but remove it from the live journal path before clearing the latch. Prefer an
atomic rename on the same filesystem, recording the before/after path and checksum. Alternatively,
move it to a protected evidence directory on that filesystem or configure `shareRecoveryFile` to a
fresh empty path. Never replace the live path with repaired content silently:

```console
sudo sha256sum -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE
sudo mv -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE \
  REPLACE_WITH_ABSOLUTE_RECOVERY_FILE.corrupt-evidence-YYYYMMDDTHHMMSSZ
sudo sha256sum -- \
  REPLACE_WITH_ABSOLUTE_RECOVERY_FILE.corrupt-evidence-YYYYMMDDTHHMMSSZ
```

#### 4. Acknowledge only after reconciliation

Continue only after all of these are complete:

- the reviewed import and its manifest/count are verified;
- the corrupt active path has a recorded disposition; and
- every uncertain sidecar record has been reconciled against PostgreSQL.

Use the same configuration as the service. Do not manually delete the latch, incident metadata,
sidecars or journal evidence:

```console
cd REPLACE_WITH_MININGCORE_INSTALL_DIRECTORY
./Miningcore -c /etc/miningcore/config.json --verify-share-recovery-state
echo "verify_exit=$?"
./Miningcore -c /etc/miningcore/config.json --acknowledge-share-recovery-state
echo "acknowledge_exit=$?"
```

Both commands return status 74 when evidence is incomplete or unsafe. Acknowledgement:

1. repeats structural verification;
2. force-durably publishes an immutable `.acknowledged` anchor for the retained chain;
3. removes the active `.fatal` latch; and
4. synchronizes the state directory.

This process is idempotent if interrupted. It does not delete evidence or perform database
reconciliation for the operator. Startup accepts the acknowledged chain only while its latest anchor
covers every retained incident and every referenced sidecar still passes complete hash, framing and
record-count validation. Later incidents extend from that tip and require another reconciliation and
acknowledgement.

#### Legacy evidence and recovery locks

Prerelease v2-only sets use a v4 legacy-set anchor that preserves the original evidence. Do not rename
or rewrite those files.

Startup inspection, incident publication and acknowledgement share a cross-process `.mutation.lock`
in the recovery-state directory. Leave it in place. If Miningcore or another recovery command owns
the lock, stop the competing process or wait; never bypass the lock or edit the state by hand.

### Restart and verify

Finally start Miningcore and inspect its complete startup, API health and pool state:

```console
sudo systemctl reset-failed miningcore
sudo systemctl start miningcore
sudo systemctl status miningcore --no-pager -l
sudo journalctl -u miningcore -b --no-pager -n 200
```

If the database session disappeared during the outage, the durable payout-manager ownership marker
may correctly remain set even though PostgreSQL is healthy again. Do not clear it merely to make the
service start. Reconcile any possibly submitted wallet transaction, prove the old process is dead,
then follow [Recover payout-manager ownership safely](#recover-payout-manager-ownership-safely).

After recovery, verify a fresh PostgreSQL backup, retain the incident logs, and add monitoring for
filesystem bytes, inodes, the configured recovery-journal filesystem, PostgreSQL readiness, daemon
RPC readiness and the Miningcore service.
Miningcore's [native file rotation](configuration.md#log-files-and-rotation) bounds its own configured
file targets, but database, daemon, journal and reverse-proxy storage still require separate policy.

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
WHERE relation.oid = to_regclass('payout_manager_ownership');"
```

Stop if the relation does not resolve or resolves to an unexpected schema. Replace
`REPLACE_WITH_SCHEMA` in this separate inspection command with the discovered `resolved_schema`:

```console
psql -h REPLACE_WITH_HOST -U REPLACE_WITH_MININGCORE_ROLE \
  -d REPLACE_WITH_DATABASE -x -c "
SELECT id, generation, owner_id, owner_host, owner_process_id,
       acquired, released
FROM REPLACE_WITH_SCHEMA.payout_manager_ownership
WHERE id = 1;"
```

Use the schema identifier exactly as PostgreSQL reports it. Double-quote mixed-case or otherwise
non-standard identifiers. Within the double-quoted `psql -c` shell command shown above, escape those
quotes as `\"PoolData\".payout_manager_ownership`. If an administrative role is required later for
the guarded update, keep using this explicitly inspected schema; do not rely on the administrator's
different `search_path`.

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

### Reconcile a Bitcoin-family payout

Use the bounded log window established above. The following read-only query lists transaction IDs
that Miningcore received from the wallet and durably persisted during that window. Replace the
schema and timestamps with the values already inspected; keep the explicit schema even when it is
`public`:

```sql
SELECT batch.poolid,
       batch.transactionconfirmationdata AS transaction_id,
       batch.created AS batch_created,
       COUNT(payment.id) AS recipient_count,
       COALESCE(SUM(payment.amount), 0) AS recorded_amount
FROM REPLACE_WITH_SCHEMA.payment_batches AS batch
LEFT JOIN REPLACE_WITH_SCHEMA.payments AS payment
  ON payment.poolid = batch.poolid
 AND payment.transactionconfirmationdata = batch.transactionconfirmationdata
WHERE batch.created >= TIMESTAMPTZ 'REPLACE_WITH_WINDOW_START'
  AND batch.created <= TIMESTAMPTZ 'REPLACE_WITH_WINDOW_END'
GROUP BY batch.poolid, batch.transactionconfirmationdata, batch.created
ORDER BY batch.created, batch.poolid, batch.transactionconfirmationdata;
```

Inspect the recorded recipients for one candidate transaction without changing accounting data:

```sql
SELECT poolid,
       address,
       amount,
       transactionconfirmationdata AS transaction_id,
       created
FROM REPLACE_WITH_SCHEMA.payments
WHERE poolid = 'REPLACE_WITH_POOL_ID'
  AND transactionconfirmationdata = 'REPLACE_WITH_TRANSACTION_ID'
ORDER BY id;
```

A `payment_batches` row, any public `payments` rows and the corresponding balance resets are committed
together. Miningcore deliberately omits active, positive-percentage `rewardRecipients` from public
payment history, so a batch with no matching `payments` rows can be valid when every balance
represented by that transaction belonged to an active reward recipient. A zero-percent entry is
inactive and remains visible in payment history if the same address earns a miner payout. This is
especially relevant to a per-recipient payout path in which one wallet transaction pays only one
reward recipient.

Treat a zero-public-payment batch as requiring reconciliation, not as corruption by itself. Inspect
the positive-percentage `rewardRecipients` configuration that was active at payout time, the
transaction's wallet outputs, the bounded Miningcore log and nearby `balance_changes` rows whose
usage is
`Balance reset after payment`. Current configuration alone is insufficient if reward recipients
changed after the payout, and timestamp proximity is supporting evidence rather than a transaction-ID
link. If any represented wallet recipient was not an active, positive-percentage reward recipient,
or the evidence is incomplete, stop and investigate before releasing ownership.

```sql
SELECT poolid,
       address,
       amount,
       usage,
       created
FROM REPLACE_WITH_SCHEMA.balance_changes
WHERE poolid = 'REPLACE_WITH_POOL_ID'
  AND usage = 'Balance reset after payment'
  AND created >= TIMESTAMPTZ 'REPLACE_WITH_BATCH_TIME_MINUS_MARGIN'
  AND created <= TIMESTAMPTZ 'REPLACE_WITH_BATCH_TIME_PLUS_MARGIN'
ORDER BY created, id;
```

No matching batch does **not** prove that the wallet submitted nothing. When a transport failure
loses the wallet response, Miningcore may never receive the transaction ID and therefore cannot
persist it; use the unknown-ID procedure below.

For a known transaction ID, query the node mempool and the exact wallet that Miningcore uses. These
commands are read-only. Supply the same RPC authentication, network and wallet-selection arguments
as the production daemon; omit `-rpcwallet` only when that daemon has no named wallet:

```console
TXID='REPLACE_WITH_TRANSACTION_ID'

bitcoin-cli getmempoolentry "$TXID"
bitcoin-cli -rpcwallet='REPLACE_WITH_WALLET' gettransaction "$TXID" true
```

Interpret the two results together:

- A valid `getmempoolentry` result means the node accepted the transaction. An `unbroadcast: true`
  field only means that no peer has acknowledged initial relay yet; it is not a wallet-only payout.
- `getmempoolentry` returning transaction-not-found while `gettransaction` reports positive
  confirmations means the payout is mined and completed.
- Transaction-not-found plus zero confirmations means the wallet knows the transaction but the
  node has not accepted or mined it. Inspect `walletconflicts`, the raw transaction and daemon
  configuration before deciding whether to rebroadcast it.
- Negative confirmations or a non-empty conflicting-wallet transaction require conflict
  reconciliation. Do not clear ownership merely because the original transaction is absent.

For an unknown transaction ID, search the production wallet over the same bounded UTC window.
Increase the `listtransactions` count when the wallet has more than 1,000 recent entries. The `jq`
filter is optional but makes sent transactions easier to group by ID:

```console
FROM_EPOCH=$(date --utc --date='REPLACE_WITH_WINDOW_START' +%s)
TO_EPOCH=$(date --utc --date='REPLACE_WITH_WINDOW_END' +%s)

bitcoin-cli -rpcwallet='REPLACE_WITH_WALLET' \
  listtransactions '*' 1000 0 true |
jq --argjson from "$FROM_EPOCH" --argjson to "$TO_EPOCH" '
  [.[]
   | select(.category == "send")
   | select(.time >= $from and .time <= $to)]
  | group_by(.txid)
  | map({
      transaction_id: .[0].txid,
      time: .[0].time,
      confirmations: .[0].confirmations,
      wallet_conflicts: .[0].walletconflicts,
      recipients: map({address, amount, fee})
    })'
```

Compare candidate recipients and amounts with the bounded Miningcore logs and the unpaid balances
that triggered the cycle. Wallet send amounts are normally negative, fees may appear on only one
entry, and a single `sendmany` transaction can have several recipient entries. Run the known-ID
queries for every candidate. If the initial search finds nothing, expand the wallet-history range
and inspect daemon or RPC-proxy logs; absence from the first page is not proof of no submission.

If a known transaction is wallet-only, fully signed, conflict-free and confirmed to be the exact
persisted payout, rebroadcasting the **same raw transaction** preserves its transaction ID. Inspect
its decoded outputs before taking the mutating step:

```console
RAW_TX=$(bitcoin-cli -rpcwallet='REPLACE_WITH_WALLET' \
  gettransaction "$TXID" true | jq -er '.hex')

bitcoin-cli decoderawtransaction "$RAW_TX"
# After verifying every output and correcting the cause of failed relay:
bitcoin-cli sendrawtransaction "$RAW_TX"
```

Never run `sendmany` or `sendtoaddress` as a substitute for rebroadcasting a known payout: those
commands create a new financial operation. If the original transaction cannot safely be broadcast,
document and complete an operator-approved compensation procedure before releasing ownership. Do
not alter Miningcore balances, payments or batch rows ad hoc.

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

If mining and share recording must resume before wallet reconciliation is complete, pause the
payout-manager pipeline without violating the deployment's startup validation:

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

Validate the complete JSON before starting Miningcore. This emergency configuration pauses the
entire payout-manager pipeline: pending-block classification, uncertain merged-block reconciliation,
maturity processing, balance credit and wallet payouts. Newly persisted block candidates remain in
PostgreSQL and are processed after the original configuration is restored. It does not clear or
supersede the durable marker. Keep this mode temporary, inspect the pending block backlog before
re-enabling the pipeline, and restore the original configuration only after ownership recovery is
complete and exactly one designated node is ready to acquire it.

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
recovery imports. Normal startup checks enabled pool IDs; recovery checks every configured recovery
pool ID because enabled state is deliberately discarded. A missing partition fails before Stratum
opens or recovery data is imported. Sender-only share-relay nodes skip this local check because their
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
