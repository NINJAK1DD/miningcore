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

If the incident produced `Share Recorder Policy Fallback`, `Fatal share-recovery fallback failure`
or recovery-journal errors, reconcile the journal before restarting Miningcore. Use the
absolute journal path reported when the Share Recorder came online. A dual-target failure exits
with status 74 and creates an independent, hashed fatal latch below the service state directory;
the exact path is printed in the fatal log and alert. The supplied systemd unit deliberately will
not restart it, and normal Miningcore startup will refuse to proceed while the latch exists or its
state cannot be read with certainty. A normal fallback email proves
only that the batch which emitted it was force-flushed; it does not prove an earlier batch succeeded
or reveal shares that failed before reaching either durable target. Review the entire outage window
for both successful fallback and fatal fallback messages.

Stop Miningcore before inspecting or importing an active journal. Record its metadata and checksum,
then preserve the original as incident evidence:

```console
sudo systemctl stop miningcore
sudo stat -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE
sudo sha256sum -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE
sudo cp --preserve=all -- REPLACE_WITH_ABSOLUTE_RECOVERY_FILE \
  REPLACE_WITH_EVIDENCE_COPY
```

Do not edit, truncate or import a corrupt original in place. A missing final newline, invalid JSON,
or batch sequence/chain/count/hash mismatch can represent a partial write or replay. Older unframed journals can only prove
their final newline boundary, not that the final multi-record batch was complete. Retain the
original and create a separate repaired input containing
only complete, operator-reviewed records; do not guess missing fields or add braces merely to make a
fragment parse. Ordinary share loss can affect reward accounting in proportional and PPLNS schemes,
so do not dismiss incomplete records merely because no block candidate is present.

Run the one-shot importer against the reviewed source only after PostgreSQL is healthy:

```console
cd REPLACE_WITH_MININGCORE_INSTALL_DIRECTORY
./Miningcore -c /etc/miningcore/config.json \
  -rs REPLACE_WITH_REVIEWED_RECOVERY_FILE
```

The importer validates every versioned frame's markers, contiguous sequence, previous-frame link,
count, record SHA-256 and deterministic frame digest before opening its transaction, then ignores
those already-verified comment lines while deserializing records. Hash validation normalises line
endings to `\n`, so it protects logical records rather than preserving the original physical newline
encoding. It
commits all records and its
SHA-256 manifest atomically. Before the transaction it writes an independent path-scoped import
marker; after commit it advances a durable retirement state machine through `Committed`,
`ArchiveDurable`, `AnchorRetirementAuthorised`, and `AnchorRetired`, retaining the expected terminal
sequence and digest throughout. It atomically renames the source with an `.imported-*` suffix,
synchronises the source directory, retires the terminal anchor only after durable authorisation,
and removes and synchronises the marker last.
Normal startup and fallback appends are blocked while an import marker remains. Before any
non-pending marker can retire the source or its terminal anchor, Miningcore opens a fresh PostgreSQL
connection and re-confirms the exact `share_recovery_imports` file hash, filename and record count.
If that proof is absent or unavailable, the source, marker and anchor remain untouched. If recovery
reports an archive, directory-sync or anchor-retirement failure, do not edit, replace or grow the source:
rerun the same recovery command with the same configuration and exact source path. Miningcore
revalidates the complete chain, terminal anchor, semantic hash and record count before resuming
retirement, and retains the committed marker if any property changed. A symlink or hard-link alias of
the configured journal is rejected because it could otherwise acquire different path-scoped safety
state. A stable symlink for the configured parent directory is supported: import reads and archive
retirement remain bound to the physical directory retained when ownership was acquired. Retargeting
that parent fails closed before database import or destructive retirement. Final-component symlinks,
FIFOs and other non-regular sources are rejected before database access. Miningcore resumes retirement and uses the database manifest to avoid reinserting already
committed records. Retain
the archive and confirm the matching `share_recovery_imports` row and record count. Do not blindly
import unexplained journals from old working directories or previous deployments; reconcile their
origin and existing manifest first. Manifests identify whole semantic files, not individual shares:
importing `A` and then an overlapping `A+B` source can duplicate `A`, so never combine or import
overlapping recovery sets.

An `Uncertain PostgreSQL share commit` incident is deliberately different from an ordinary recovery
journal. Miningcore cannot prove whether PostgreSQL committed, so it does **not** append those shares
to the importable journal. The small status-74 fatal latch names an exact-share sidecar and its
expected SHA-256. That sidecar contains one `shareJsonBase64=` record per line for read-only
reconciliation. Verify the sidecar hash, decode and compare those records with PostgreSQL; do not
copy them into a journal or replay them unless reconciliation proves they are absent. A
`detailState=hash-pending` latch is authoritative even if its sidecar is absent or partial. Earlier
v2 incidents may call the equivalent state `incomplete`. Preserve the fixed latch, every immutable
`.incident` metadata record, sidecar and temporary incident evidence until every record has a
conclusive disposition.

Use the read-only verifier before manual reconciliation. Supply the same configuration used by the
service so Miningcore resolves the exact recovery journal and independent state directory:

```console
cd REPLACE_WITH_MININGCORE_INSTALL_DIRECTORY
./Miningcore -c /etc/miningcore/config.json --verify-share-recovery-state
echo "exit=$?"
```

The command enumerates every path-scoped `.incident` record, validates its metadata and v3
sequence/previous-digest chain against the fixed latch's expected count and tip, verifies the
referenced sidecar SHA-256, decodes every Base64 JSON share and checks the record count. Sidecar
records are allocation-bounded to 1,048,576 characters and are parsed while the same restrictive
file handle incrementally calculates the hash. Stable identity and length checks reject concurrent
modification or path replacement. The smaller `.fatal`, `.incident`, and `.acknowledged` metadata
files are likewise
read as strict UTF-8 from one restrictive no-follow handle, with an exact cumulatively enforced
64-KiB raw-byte total (including physical CRLF bytes), a 16-KiB per-line limit, and the same stable
identity/path checks. Memory exhaustion is reported as a failed verification rather
than allowing the command to continue. It returns
status 74 when evidence is missing, malformed, hash-pending or otherwise incomplete. The one exact
recoverable exception is a completed, verified incident and sidecar whose durable latch still records
the immediately preceding hash-pending state; startup or acknowledgement safely completes that latch
transition under the mutation lock and still requires operator reconciliation. A successful
verification proves only that the recorded evidence is structurally complete; it cannot prove that
the uncertain records were reconciled against PostgreSQL. Complete that database comparison before
acknowledging an active fatal latch. A first v3 incident anchors every legacy-v2 incident then present,
but evidence lost before that upgrade cannot be detected retroactively. The verifier never edits,
imports or deletes recovery files.

Frame chains detect duplicated, missing and reordered middle frames. New writes also commit the
expected final sequence and digest under the independently stored
`shareRecoveryStateDirectory/share-recovery-terminal` directory. Startup and import reject a
shorter valid prefix, so preserve this anchor with the journal during inspection and restore. A
legacy/v1 journal cannot gain retroactive protection for history written before its first anchored
v2 append. Retain the incident checksum captured before repair/import and compare it with backups or
monitoring evidence when earlier truncation is plausible. Miningcore does not use a best-effort
existence check for terminal or import markers: it must successfully enumerate the exact state
directory before absence is accepted. A directory, symlink, malformed or inaccessible marker is
unreconciled state and blocks startup with status 74.

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

Only after the reviewed import, its manifest/count, the active-path disposition, and every uncertain
sidecar record have been reconciled, verify and durably acknowledge the incident chain. Use the same
configuration as the service; do not delete the fatal latch, incident metadata, sidecars, or journal
evidence manually:

```console
cd REPLACE_WITH_MININGCORE_INSTALL_DIRECTORY
./Miningcore -c /etc/miningcore/config.json --verify-share-recovery-state
echo "verify_exit=$?"
./Miningcore -c /etc/miningcore/config.json --acknowledge-share-recovery-state
echo "acknowledge_exit=$?"
```

Both commands return status 74 when evidence is incomplete or unsafe. The acknowledgement command
performs the structural verification again, atomically and force-durably publishes an immutable
`.acknowledged` anchor covering the retained chain, then removes the active `.fatal` latch and
synchronises its directory. It is idempotent if the process stops between those steps. Successful
acknowledgement does not delete evidence and does not prove the database reconciliation on the
operator's behalf. Startup accepts an acknowledged chain only when its latest anchor covers every
retained incident; deleting or modifying covered evidence blocks startup. Later fatal incidents
extend from that acknowledged tip and require a new acknowledgement after reconciliation. Startup
also re-hashes and parses every retained sidecar on every launch, so a missing, truncated or
replaced sidecar remains a status-74 failure even after acknowledgement.

Prerelease v2-only incident sets are acknowledged through a v4 legacy-set anchor that preserves the
original v2 evidence and lets the next v3 incident extend from it. Do not rename or rewrite those
legacy files. Startup inspection, incident publication and acknowledgement share a path-scoped,
cross-process `.mutation.lock` in the recovery-state directory. Leave that file in place. If the
service or another recovery command owns it, stop the competing process or wait for it to finish;
do not bypass the lock or edit the state by hand.

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

A `payment_batches` row and its `payments` rows are committed together. A batch with no matching
payment rows is an accounting anomaly: stop and investigate instead of releasing ownership. No
matching batch does **not** prove that the wallet submitted nothing. When a transport failure loses
the wallet response, Miningcore may never receive the transaction ID and therefore cannot persist
it; use the unknown-ID procedure below.

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
