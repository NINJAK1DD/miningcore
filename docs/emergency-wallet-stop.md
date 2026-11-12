# Database emergency wallet payout stop (protocol 1)

This mandatory control stops **new wallet payout batches** across cooperating
Miningcore processes using one PostgreSQL database. It starts paused and has no
configuration bypass. It supplements ownership, reconciliation and coordinated shutdown.

## Scope and race boundary

The administrator-owned `miningcore_payout_control` schema is database-wide,
independent of application search path, pool, coin or wallet. Every handler invoked
by `PayoutManager` passes through it. One admission covers a complete
`IPayoutHandler.PayoutAsync` call over the selected balance snapshot: **all requests
in an already admitted batch may still run after pause**, including retries,
per-recipient sends, wallet preparation and asynchronous daemon work. This is a
batch stop, not per-RPC cancellation or a wallet firewall.

Admission and pause serialize on the same singleton row with `FOR UPDATE`.
Committed admission is the ordering boundary. If pause wins, the handler is never
called. Otherwise the durable batch stays outstanding until the handler conclusively
finishes and accounting persists. Pause commits without waiting. Resume and further
admission refuse outstanding batches. Keep operator transactions short. Admission
uses a two-second server lock timeout: a confirmed lock timeout defers the batch
until the next cycle, without retaining ownership solely for that attempt.
Completion does not lock the state row, so an open pause transaction cannot prevent
a conclusive payout from completing. Completion is idempotent for the same batch,
owner and generation; at most three attempts use the original guard session.

The gap between admission and a wallet request cannot be atomic. A suspended or
partitioned process can begin an admitted request much later. A dead database
session, an empty advisory-lock inventory or successful pause response does **not**
prove wallet idleness. For immediate quiescence, stop and independently verify every
wallet-capable process. Terminating requests can create uncertain outcomes.

The scope excludes other databases/clusters, restored copies, old binaries,
custom callers bypassing the manager, wallet consoles and other wallet software.
Never share spending wallets between independent payout databases. Where unavoidable,
coordinated shutdown and wallet access controls remain necessary across all of them.
Use primary PostgreSQL with direct sessions, not read replicas or transaction-pooling
proxies. Failover/restore requires shutdown and reconciliation; stale controls are
not authoritative.

Relay-only miners do not broadcast wallet payouts: pause the payout consumer's
database. Independent consumers need independent controls and deployment inventories.
Shares, PPS liabilities, merged parent/auxiliary accounting, classification and
balance credits continue normally while admission is paused. Database loss can
still interrupt accounting. Existing process-local enable/disable controls retain
their accounting semantics. The restart-only `paymentProcessing.paused` proposal
in PR #262 is not implemented or redefined here; where supported it is an additional
local gate, never database-wide protection. Check the exact deployed binary.

**Direct-coinbase rewards and already-broadcast transactions cannot be recalled.**
Mining-side direct allocation and block submission are outside this wallet control.

## Coordinated installation, upgrade and rollback

1. Inventory and stop every wallet-capable service, container, relay payout consumer,
   job and fallback/older instance. Preserve coordinated shutdown and exact-binary
   pre-start checks. Reconcile uncertain outcomes before ownership release.
2. Back up financial/audit state and apply normal ownership migrations. Run
   `src/Miningcore/Persistence/Postgres/Scripts/add_wallet_payout_control.sql` once
   using `psql -X -v ON_ERROR_STOP=1`, as a separate trusted administrator with the
   application's schema first in `search_path`. The application must not be a
   superuser, have administrative attributes or be able to assume that administrator.
   The migration rejects that relationship, removes default ACLs, grants runtime
   read/admit/complete rights and starts **paused**. Existing schemas are rejected:
   rerunning never resets history, adopts lookalikes or unpauses the control.
   Supply the exact runtime login explicitly (it need not own the payout tables):

   ```sql
   -- In the same administrator psql session, before \i of the migration:
   \set app_role miningcore_service
   SELECT set_config('miningcore.wallet_control_app_role', :'app_role', false);
   \i src/Miningcore/Persistence/Postgres/Scripts/add_wallet_payout_control.sql
   ```

   The migration and read-only startup preflight reject effective write/operator
   access, including `pg_write_all_data`, inherited privileges and assumable roles.
   Missing state, incompatible protocol or missing admission/completion permissions
   fail preflight **before acquiring durable ownership**. Intentionally paused
   state passes. Fresh installations require this separate administrator step too.
3. Provision an individually attributable operator LOGIN through your credential
   manager, using SCRAM and verified TLS or a secured local socket. Keep credentials
   out of application configuration, logs and command lines. As administrator, grant
   the following, substituting your operator identity:

   ```sql
   GRANT USAGE ON SCHEMA miningcore_payout_control TO wallet_stop_operator;
   GRANT SELECT ON ALL TABLES IN SCHEMA miningcore_payout_control TO wallet_stop_operator;
   GRANT EXECUTE ON FUNCTION
       miningcore_payout_control.set_paused(boolean,bigint,text),
       miningcore_payout_control.reconcile_batch(uuid,bigint,text)
       TO wallet_stop_operator;
   ```

   Never grant operator rights to PUBLIC, the application or its assumable roles.
   Do not grant table writes, schema CREATE or object ownership. Review inherited
   memberships/default ACLs. Test with actual logins: application pause/resume/reconcile
   and direct DML must fail; operator direct DML must fail. Administrators remain
   trusted and can alter/drop controls; export audit events to independent protected
   storage for that threat model.
4. Deploy protocol-1 binaries everywhere before relying on the marker. Missing,
   denied, unsupported or unreadable control state fails closed. Verify exact
   binaries, grants and paused state in staging before live startup. Ambiguous
   admission retains ownership even though no new wallet batch was started.
   Explicit statement rejection is different: a PostgreSQL `ERROR` rolls back the
   autocommit admission and leaves ownership releasable. Fatal connection failures,
   client cancellations, timeouts and lost responses remain uncertain. No wallet
   request is made in either case.
5. Verify accounting/monitoring while paused, then explicitly resume. Older versions
   ignore the marker even when sharing advisory ownership. Mixed deployments are
   **unprotected**.

Rollback requires another verified coordinated stop. Keep the control paused,
preserve history/outstanding batches and reconcile wallets. Return to coordinated
shutdown as the safety procedure for older binaries; do not drop the marker to make
them run or describe them as protected. Restore requires stopped processes, preserved
evidence, reviewed role ownership/ACLs and paused control before reconciliation/resume.

## Authenticated and audited operator protocol

Use the dedicated login. Only committed results count as acknowledged. After a
disconnect/timeout, inspect state/events; never assume success or blindly retry
resume. Audit records use authenticated `session_user`, unaffected by `SET ROLE`.
Incident references must be nonempty, at most 200 characters and contain no secrets.

```sql
SELECT * FROM miningcore_payout_control.state;
SELECT * FROM miningcore_payout_control.batches WHERE completed IS NULL;
-- Substitute the revision reviewed, not an automatically retried value.
SELECT miningcore_payout_control.set_paused(true, 1, 'INC-123 stop');
-- Review outstanding batches and deployment coverage before resume:
SELECT miningcore_payout_control.set_paused(false, 2, 'INC-123 approved resume');
SELECT * FROM miningcore_payout_control.events ORDER BY revision DESC LIMIT 100;
```

Successful commands atomically increment revision and append an event, including
repeated requests. Audit failure rolls back the state change. Stale revisions fail
so delayed resume cannot overwrite a newer stop. There is no HTTP write endpoint:
existing API network filtering is not operator authentication.

Pause/resume never changes payout ownership, payments, balances or uncertainty
evidence. Conclusive completion closes the admitted batch even while paused, letting
successful accounting finish. Unknown outcomes retain **both** ownership protection
and the batch. Database/session loss never authorizes replacement startup.

After independently establishing process death (including disconnected remote
instances), inspect wallet/chain history and reconcile payments/balances using the
existing recovery procedure. Only then resolve the exact outstanding batch while paused:

```sql
BEGIN;
-- If false, ROLLBACK and stop; do not wait for another owner.
SELECT pg_try_advisory_xact_lock(19779, 5259609);
-- Only after true AND independent process-death/wallet/accounting verification:
SELECT miningcore_payout_control.reconcile_batch(
    '00000000-0000-0000-0000-000000000000', 3, 'INC-123 reconciled evidence reference');
COMMIT;
```

Substitute the actual batch UUID/revision. This audited resolution is not a payment
retry or accounting adjustment and **never clears ownership**. Perform guarded
ownership recovery separately, retaining its confirmations/evidence. Resume is
another explicit action. No TTL, expiry, reconciliation or failover is automatic.
Reconcile outstanding batches **before ownership recovery and before restart**:
reconciliation needs the advisory lock. The guided recovery assistant reports
outstanding or unreadable control evidence and refuses release until resolved.

Each batch stores a bounded pool ID and a SHA-256 digest of the selected recipient/
amount snapshot, using sorted, length-prefixed fields and invariant decimal encoding.
This is an incident correlation reference, not proof of payment or a replacement
for wallet receipts. It contains no credentials or raw recipient addresses; treat
the digest as financial metadata (it is not encryption). The HTTP state endpoint
does not expose it. A completion-recording failure reports a control incident,
preserves the original handler failure if any, and does not relabel paid recipients
as having uncertain wallet outcomes.

## Retention and archival

Retain completed batches and all events indefinitely by default. Do not delete or
expire outstanding batches. Monitor table/index size, oldest outstanding admission,
backup age and restore results. Plan capacity for one row per admitted handler call.
Export completed batches and events periodically to access-controlled immutable
storage with database identity, schema version, export cutoff, row counts and a
cryptographic manifest; verify restoration before treating an export as archival.
Audit events remain append-only. This version provides no online deletion or
automatic retention job. If storage pressure requires pruning completed history,
stop every writer, obtain a reviewed administrator-only archival migration that
retains the verified audit trail and financial references, and test restore and
reconciliation before rollout. Application/operator roles must never receive DELETE
or TRUNCATE for retention purposes.

## State and alerts

`GET /api/admin/payment/wallet-control` returns `protocolVersion`, `revision`,
`paused` and `outstandingBatches` from one snapshot, without addresses, incident
text or identity. Missing/unreadable/unsupported state returns HTTP 503 with
`state: "unknown"` and `newWalletBatchesAllowed: false`: blocked/unknown is **not
confirmed paused**. It does not claim local enablement, ownership health, active
accounting or deployment coverage.

Monitor this administrative endpoint, including non-200 results, or the tables with
a read-only login. Alert on unknown state, unexpected pause, long-lived outstanding
batches and unexpected revisions/operators. Runtime warnings explicitly identify
the database wallet gate and say mining-side accounting remains enabled; admission
logs include batch UUIDs. Unknown outcomes retain the fatal diagnostic path. Monitor
share persistence separately; display local configuration, database admission and
accounting health separately. Zero outstanding batches describes only cooperating
callers, not external wallet idleness.

## Validation and references

`WalletPayoutControlIntegrationTests` uses the documented
[isolated PostgreSQL/real-daemon lab](../scripts/test-lab/README.md): authenticated
application/operator roles, competing managers, both admission/pause orderings,
stale resume, audit rollback, missing state, session loss, restart, Bitcoin accounting
and an actual broadcast whose response is lost. Run focused checks and the full suite.

The design follows PostgreSQL's [row-lock ordering](https://www.postgresql.org/docs/18/explicit-locking.html)
and [security-definer rules](https://www.postgresql.org/docs/18/sql-createfunction.html#SQL-CREATEFUNCTION-SECURITY).
Database serialization establishes admission order, not atomicity with an external
wallet. Durable uncertainty and coordinated deployment are part of the protocol.

### Verified lab run: 12 November 2026

The isolated Ubuntu 26.04 lab used SDK 10.0.404, .NET/ASP.NET Core 10.0.15,
Bitcoin Core 31.1, Litecoin 0.21.5.8, Dogecoin 1.14.9 and disposable PostgreSQL.
The complete native/managed build had zero warnings and errors. Final test revision
`107615a4a09ffe337ec72393a5a97cc7ed37cca7` passed **6,297 tests, zero failures,
28 skips** (6,325 total). The only change after the complete production rebuild was
the reviewed integration-collection membership entry; the test project was rebuilt
and the entire suite rerun. All 19 new regression cases passed without skips,
including seven live PostgreSQL/process/Bitcoin scenarios and five transport-level
wallet setup cases. Documentation links and diff whitespace checks passed.

The 28 skips are opt-in daemon/GPU, privileged ownership/trust-store and benchmark
fixtures; they are not claimed as verified. The tested binaries and databases were
isolated from production. Evidence is retained in the lab checkout's
`build/test-lab/build.log`, `test-rebuild.log` and `wallet-control-final.trx`.

This validates the six original acceptance criteria of #291. The four scenarios
added to that issue during implementation remain **unverified and unchecked**:
combined locally paused/unpaused deployments of the unmerged #262 feature,
extended paused relay operation, a large accumulated block backlog across restart,
and PostgreSQL interruption during paused PPS arrivals. Keep #291 open for those
combined integration scenarios; this independent protocol change does not establish
their acceptance or expand #262. These four scenarios are **production release
blockers** for the combined local-pause, relay/backlog and paused-PPS topologies;
a green CI run does not remove that restriction. Keep such deployments on the
documented coordinated-shutdown procedure until the combined implementation is
validated. Live payout compatibility for the affected non-Bitcoin wallet families
is also a release gate: transport fixtures alone do not establish daemon support.

Empty-argument probes now follow peer/recipient checks and fail conclusively on
transport loss; unexpected success still requires reconciliation. Parameterized
`z_sendmany` probes remain conservative. Xelis activation is non-spending and
defers payout until a later cycle, then checks the wallet's scanned topoheight
against the daemon before building transactions, following the official
[Xelis wallet API](https://docs.xelis.io/developers-api/wallet#get_topoheight).
