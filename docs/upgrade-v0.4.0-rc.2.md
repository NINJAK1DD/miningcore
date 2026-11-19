# Upgrading to v0.4.0-rc.2

Use this checklist for an isolated rehearsal first. The stable quick start selects
v0.3.0. Selecting RC.2 is explicit; a release-preparation PR is not a published
archive, and a successful build is not evidence that a real wallet is reconciled.

## Select and stage the candidate

1. Record the old binary/image identity, configuration, service overrides, database
   roles, filesystems holding recovery journals, wallet backups and **every process**
   sharing the database or wallet. Include remote relay senders and payout consumers.
2. For an archive, explicitly select the published RC.2 in
   [Choose a version](releases.md#choose-a-version), verify checksums and provenance,
   and stage into a new versioned directory, without replacing the active installation.
   Match Ubuntu 22.04 or 26.04 x64; 24.04 uses a source build. Compare the peeled
   release commit from the [independent lookup](releases.md#resolve-the-release-source-commit)
   to BUILD-INFO and the candidate's `--version`; check the build's host target.
   For source, build from that immutable commit. For packaged containers, pin the RC
   image digest, compare its revision and use its matching SQL. Neither type of
   container migrates the database. Do not use the stable `latest` alias for RC.2.
3. Verify **both .NET and ASP.NET Core 10.0.15 or later within .NET 10** under the
   service's actual host. A successful SDK build or `--version` alone does not prove
   the service loads the serviced assemblies. Run the [published-dependency smoke
   checks](dependency-security.md#published-dependency-and-runtime-acceptance).
4. Stage the existing protected configuration, not a new example. Legacy
   `timestamp without time zone` shares require explicit
   `persistence.postgres.enableLegacyTimestamps: true`; use false for the modern
   timestamptz schema. Do not convert existing timestamps during deployment.
   Review [PostgreSQL transport policy](postgres-tls.md) with Npgsql 10.

## Database preparation

Stop and independently verify all writers and wallet-capable processes. Take a
restorable backup of the quiescent database with owners, grants and audit history.
Prove a restore in isolation using the [cross-cluster acceptance procedure](database.md#cross-cluster-release-acceptance).
Use SQL from the selected candidate: archive `migrations/`, packaged image
`/app/migrations/`, or source `src/Miningcore/Persistence/Postgres/Scripts/`.
Keep SQL errors fatal (`psql -X -v ON_ERROR_STOP=1`). Never apply every file in
the directory in alphabetical order.

| Starting point | Required decision |
| --- | --- |
| New empty database | Import RC.2's `createdb.sql` once. It includes the guided-recovery audit structures. Wallet admission still needs the separate administrator installation below. |
| Existing RC.1 | Never rerun `createdb.sql`. Preserve ownership markers, receipts and arithmetic version. Install `add_payout_recovery_audit.sql` for guided recovery; without it a clean start can succeed but guided release refuses. Then provision wallet control for wallet payout managers. |
| v0.3.0 or older | First identify missing earlier prerequisites with the [RC.1 migration matrix](upgrade-v0.4.0-rc.1.md#database-migration-matrix) and earlier version boundaries. Apply only the required prerequisites from the candidate, then follow the existing-database RC.2 steps. The RC.1 no-new-SQL row for prepared SOLO pools does **not** exempt an RC.2 wallet payout manager. |
| Interim development installation | Preserve audit history and reapply the documented [audit migration](releases.md#guided-recovery-migration-matrix) as applicable. For an existing protocol-1 control use `upgrade_wallet_payout_control_v2.sql` while paused as its trusted owner; never replay initial control installation or drop the schema. Verify existing protocol-2 owners and grants instead of reinstalling. |

For wallet payouts, follow [coordinated wallet control installation](emergency-wallet-stop.md#coordinated-installation-upgrade-and-rollback).
Install `add_wallet_payout_control.sql` only when the control is absent, as a
separate trusted administrator with the exact runtime login and application search
path. A non-superuser control owner needs effective inherited `pg_read_all_stats`;
do not grant administrative or operator privileges to the service. The database-wide
control starts **paused**. Confirm actual login permissions and paused state before
activation. Never use the RC.1 archive activation block for RC.2 or edit its version
guard: it does not perform these steps.

## Controlled startup, acceptance and rollback

- Only after the restore rehearsal, selected SQL and role checks succeed, record
  the previous deployment target and activate the staged candidate while services
  are stopped: switch the archive service's versioned symlink, select the verified
  source publish directory in the service, or recreate the container at its pinned
  digest with the original protected mounts. Check the effective service command,
  unit overrides and data paths before starting. On any partial migration, stop
  here and reconcile the schema; do not blindly rerun the cutover.
- Deploy the same validated binary to all participating nodes while stopped. Retain
  service ordering, protected files and exit-status handling. Never start old and
  new payout owners together. For a restart-only local stop use the exact cluster
  property `paymentProcessing.paused`; unknown or incorrectly cased fields under
  cluster paymentProcessing are rejected. Keep merged parent and auxiliary accounting
  switches enabled. See the [pause pre-start check](database.md#restart-only-payout-pause).
- Start in isolation with wallet admission paused. Unlike the local processor
  pause, database admission pause allows classification and accounting to continue.
  Neither control recalls already admitted sends or stops direct SOLO coinbase
  rewards. Review outstanding batches and wallet history before an authorized
  operator explicitly resumes. Do not infer wallet inactivity from a dead session.
- PPS acknowledgement now requires committed accounting or an authenticated,
  force-flushed journal. PPS relay senders need a recorder, share partitions, the
  same accounting database, bind mode and matching keys of at least 32 characters.
  Follow [relay rollout and key rotation](share-relays.md#required-pps-transport-protection-and-upgrade)
  on both ends; monitor pressure and share rates even for non-PPS senders.
- Verify listener/proxy attribution, daemon sync, accepted shares, recorder/journal
  health and parent/aux accounting. Test the configured coin and payout scheme in
  staging. [Custodial confirmations](custodial-confirmation-policy.md) can hold a
  Bitcoin-family reward pending for longer; do not lower maturity or manually credit
  the row to make a test pass. Validate the native libraries on the target host:
  affected Blake3 and keyed Blake2b pools cannot use the legacy bundled Windows DLL.
- On failure keep wallet processes stopped, preserve database, journals, wallets
  and audit evidence, and reconcile before deciding whether to restore. RC.1 and
  older binaries ignore the new wallet controls. Switching back a symlink or image
  does not make restarting safe. See [rollback and reconciliation](emergency-wallet-stop.md#coordinated-installation-upgrade-and-rollback).

## Before tagging or promoting

Record the exact release-preparation commit as it lands on `dev`. Run the
[documented lab suite](../scripts/test-lab/README.md) with PostgreSQL and the
pinned Bitcoin BLAKE2b Knots regtest daemon as well as the standard daemons;
record any optional hardware/daemon skips. Keep cross-cluster restore, wallet
reconciliation and topology-specific commissioning evidence distinct from PR CI.
After tag-triggered publication, verify the prerelease flag, archive checksums and
source identities, and the versioned container digest. Stable aliases must not move.
