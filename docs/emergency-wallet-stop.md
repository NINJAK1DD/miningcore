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
admission refuse outstanding batches. Keep operator transactions short.

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
