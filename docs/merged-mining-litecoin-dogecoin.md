# Litecoin–Dogecoin merged mining

Miningcore can run Litecoin as the parent Scrypt pool and submit the same proof of work to Dogecoin through AuxPoW.

## Requirements and miner login

This version is limited to SOLO. Configure Litecoin and Dogecoin as separate enabled pools with unique pool IDs, both using `SOLO` payment processing. On direct and relay receiver/recorder nodes, cluster-level `paymentProcessing.enabled` must also be `true`; otherwise the reconciliation and payout manager is not running. The Dogecoin pool supplies its daemon, wallet address, block classification and payout pipeline. The Litecoin pool references it:

```json
"mergedMining": {
  "enabled": true,
  "auxPoolId": "doge-solo",
  "addressParameter": "doge",
  "requireAuxAddress": true,
  "auxiliaryTemplatePollTimeoutMs": 500
}
```

Connect miners only to the Litecoin Stratum endpoint.

- Username: `LTC_ADDRESS.worker`
- Password: `d=65536;doge=DOGE_ADDRESS`

The Dogecoin daemon mines rewards to the configured Dogecoin pool wallet. Miningcore records the password-supplied Dogecoin address as the SOLO beneficiary and pays it through the existing Dogecoin payout processor after maturity.

## Share and block accounting

### Share publication and candidate persistence

A new parent job is generated when either chain changes. Each submitted Scrypt proof is checked against both targets. Once proof validation succeeds, Miningcore publishes a cleared ordinary statistical copy before starting either daemon submission; a slow or failed peer-chain path therefore cannot suppress the share or move it beyond the parent effort boundary. Litecoin and Dogecoin block submissions are independent. Accepted or transport-uncertain merged-mining blocks are synchronously persisted as block-only candidates as soon as their own submission finishes; they do not wait for the ordinary five-second share batch or ZeroMQ relay. The pool does not publish the original proof a second time. No synthetic Dogecoin share row is inserted.

### Block states and notifications

Merged Litecoin parent rows use explicit block types: `merged-parent` once accepted and
`merged-parent-uncertain` while an ambiguous parent submission awaits reconciliation. Dogecoin rows
use `auxpow` once accepted and `auxpow-claim` while a proof-specific claim is unresolved. Uncertain
rows are excluded from public block totals, last-block timestamps and effort boundaries, and do not
emit normal block-found notifications. A promoted claim emits that notification only after its
database transaction commits. Losing, expired or superseded claims do not emit the ordinary orphan
notification because they were never announced as found blocks.

On both direct and relay nodes, payout processing defers a new merged-parent row for one minute so
the ordinary five-second share buffer settles before effort or terminal status is frozen. Effort
ranges use an inclusive upper boundary, including the winning share at the exact block timestamp
without overlapping the previous interval.

### Submission reconciliation

If acceptance or the coinbase transaction is not yet available, the block stores a reconciliation
marker. The payout classifier retries `getblock` and replaces the marker with the coinbase
transaction ID before monitoring maturity.

Dogecoin proof attribution is required for both transport-ambiguous and Boolean
`submitauxblock: true` responses. A DOGE candidate is finalized only when the active child block's
`auxpow.parentblock` matches the submitted parent header. Missing proof data creates a proof-specific
claim instead of trusting the Boolean response; a different parent proof means this miner lost.
Finding the child hash alone is not sufficient.

Dogecoin blocks with `confirmations = -1` are inactive or orphaned, not payable accepted blocks.
Multiple claims can coexist for one child hash, but only the matching proof can become the finalized
AuxPoW row. Uncertain submissions need at least three definitive misses and 30 minutes before they
expire as orphaned. Active responses that repeatedly omit `auxpow.parentblock` use the same guard.

An exact active block is never orphaned merely because the daemon or RPC proxy temporarily omits its
transaction list. Coinbase lookup remains pending until the transaction ID appears or the block is
inactive or definitively absent.

## Auxiliary-address policy

### Address validation

When `requireAuxAddress` is true, authorisation fails if the address is missing or rejected by Dogecoin's `validateaddress` RPC. A bounded process-local cache remembers up to 4096 addresses that this process has positively validated. During a temporary validation-RPC outage, a reconnect using one of those exact addresses may continue; a new or previously unseen address still fails closed. The cache is deliberately not persisted and is empty after restart. The address is captured once at authorisation, so changing it requires reconnecting. The Dogecoin pool must remain enabled so its normal classifier, maturity checks and payout processor can handle auxiliary blocks.

When `requireAuxAddress` is false, a worker that omits `doge=` mines Litecoin only. If its proof also reaches the DOGE target, Miningcore deliberately does not submit that auxiliary candidate because no miner-supplied SOLO beneficiary can be attributed. It is not credited to a fallback or pool address. This avoids unattributed funds at the cost of discarding that DOGE candidate; production merged-mining pools should normally keep `requireAuxAddress` enabled.

### Pool configuration safeguards

All enabled pool coin templates are assigned before any pool is configured, so the LTC and DOGE entries may appear in either order. `addressParameter` is trimmed, defaults to `doge` when blank, and cannot be `d` or contain `;` or `=`. Definitively invalid DOGE logins use the normal failed-login ban path; a temporary DOGE validation RPC failure returns a server error without banning the miner unless the exact address has already passed validation in this process. When multiple Dogecoin daemon endpoints are configured, the merged-mining manager logs a warning and uses the first endpoint; configure one authoritative auxiliary endpoint rather than assuming failover.

## Template refresh, submission and shutdown

### Template refresh

The parent pool polls both templates even when Bitcoin Template Stream is configured, because parent
notifications do not include Dogecoin tip changes. Stream events trigger a refresh; they are not
treated as authoritative snapshots.

Miningcore accepts a freshly fetched Litecoin template with a different `previousblockhash` even if
its height decreased, because that can be a valid active-chain reorganisation. It caches the
successful startup Dogecoin template to seed the first combined job. After that, the last valid DOGE
template allows fresh LTC jobs to continue through a temporary auxiliary-daemon outage.

Startup, recurring polling, address validation, submission and ambiguity lookup have separate
timeouts. `auxiliaryTemplatePollTimeoutMs` controls recurring Dogecoin `createauxblock` calls and
defaults to 500 ms; startup synchronization retains its separate ten-second deadline.

When a startup or recurring request exceeds its deadline, Miningcore reports
`timed out after N ms` rather than the transport client's generic `Cancelled` text. Host or
shutdown cancellation is classified separately and does not place the auxiliary-template path
into a degraded state. A timeout or other failed refresh reuses the last valid DOGE template so
Litecoin mining can continue; the first fallback logs `Auxiliary template update failed`, and a
later successful refresh logs `Auxiliary template updates recovered`.

Do not raise the timeout solely because one warning appears. Confirm recovery, then inspect warning
frequency, Dogecoin synchronization, CPU and storage pressure, RPC saturation, and correlation with
payout activity. Increase the setting modestly only when a healthy local daemon consistently needs
longer. A longer deadline can delay a fresh parent Litecoin job. The 500 ms default remains the
general recommendation; an operator-specific 1000 ms setting can be reasonable when measurements
support it.

Prometheus exposes the complete startup and refresh paths, including attempts that time out or are
cancelled:

| Metric | Meaning |
| --- | --- |
| `miningcore_auxiliary_template_rpc_duration_seconds` | Histogram of `createauxblock` duration by parent `pool`, `aux_pool`, `startup`/`refresh` phase and bounded outcome; its `_count` series is the attempt count |
| `miningcore_auxiliary_template_fallback_total` | Number of healthy-to-degraded fallback episodes by parent/auxiliary pair |
| `miningcore_auxiliary_template_available` | `1` when a usable auxiliary template exists; `0` when no usable auxiliary template is available, preventing construction of a merged-mining job |
| `miningcore_auxiliary_template_degraded` | `1` while that parent uses a cached template from the named auxiliary pool; otherwise `0` |

Separate parent-pool labels prevent a healthy parent from clearing another parent's degraded state
when both reference the same auxiliary pool. Separate phase labels keep ten-second startup probes
out of recurring timeout analysis. Histogram buckets straddle the 500 ms default and extend through
the ten-second startup deadline. State gauges are reasserted on refresh so a transient telemetry
processing failure self-heals, while the fallback counter increments only on a new degraded episode.
The histogram remains bounded to ten label sets per configured parent/auxiliary pair (two phases by
five outcomes); each set exports the configured buckets plus `+Inf`, `_sum` and `_count`. For example,
the fraction of refresh attempts within 500 ms is:

```promql
sum by (pool, aux_pool) (rate(miningcore_auxiliary_template_rpc_duration_seconds_bucket{phase="refresh",le="0.5"}[5m]))
/
sum by (pool, aux_pool) (rate(miningcore_auxiliary_template_rpc_duration_seconds_count{phase="refresh"}[5m]))
```

Alert when `available` remains `0` beyond the expected daemon startup or synchronization grace
period. Use a Prometheus `for:` duration appropriate to the deployment so ordinary restarts do not
page operators. Also alert on a sustained degraded gauge, a new fallback episode, or continuing
timeout/transport-failure histogram counts. The ordinary
`miningcore_rpcrequest_execution_time` series remains useful for other RPC methods, but these
auxiliary-specific series are the authoritative view of failed and cancelled template attempts.

### Candidate ownership and deadlines

Merged share processing obtains a preparation lease before proof validation. Once local validation
finds an LTC or DOGE candidate, ownership transfers atomically to a manager operation that is
independent of the miner connection. Miner EOF, TCP reset or client cancellation cannot cancel
daemon delivery.

Submission and its attribution lookup share one manager-owned ten-second RPC deadline. Durable block
persistence is not covered by that deadline; it follows PostgreSQL timeouts, database retries and
recovery-journal write-through rules. A candidate Stratum request can therefore remain open for more
than ten seconds during storage failure. Stratum proxies should tolerate this rare candidate-only
delay. Database uniqueness makes a miner retry idempotent.

### Shutdown

During shutdown, the merged manager enters quiescing mode and:

1. Rejects new submissions before validation.
2. Waits for active validations to finish candidate handoff.
3. Observes abandoned request exceptions.
4. Drains every candidate operation.

Parent and auxiliary tasks own their complete submission, reconciliation and persistence paths.
Miningcore drains both before propagating an error, so failure on one chain cannot abandon an
accepted result from the other.

### Ambiguous submissions

Litecoin responses of JSON null, `inconclusive`, `duplicate` or `duplicate-inconclusive` are checked
with `getblock`. Malformed, missing, duplicate or null-ID parent batch responses are also ambiguous
after local proof validation. If neither inactivity nor a coinbase transaction can be established,
Miningcore persists an exact-hash `merged-parent-uncertain` row instead of discarding the candidate.

For either chain, transport ambiguity triggers `getblock` reconciliation and an uncertain marker if
the daemon remains unavailable. Explicit Dogecoin JSON-RPC errors are rejected without creating a
candidate. A resolved row is not orphaned solely because the wallet returns `gettransaction -5`
while its block cannot be proven inactive.

If active-chain checks remain unavailable for the uncertain block's lifetime, Miningcore emits one
administrative notification for that outage episode. A successful wallet response or definitive
active/inactive lookup clears the episode.

## Database migrations

### Required scripts

For an existing PostgreSQL database, stop Miningcore block writers and payout managers or schedule a
maintenance window. Apply both scripts before enabling merged mining:

- `src/Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql`
- `src/Miningcore/Persistence/Postgres/Scripts/add_payout_manager_ownership.sql`

The ownership migration is required for every payment-processing cluster in this release series,
including clusters without merged mining. It is also required for recorder/recovery-only deployments
that use the `-rs` importer.

### Migration guarantees

Both migrations are transactional, so failed validation or index creation rolls back the changes.
The AuxPoW migration uses regular `CREATE INDEX` within its transaction. It resolves the schema of
the active `blocks` relation before dropping obsolete indexes, detects legacy uncertain or duplicate
merged-mining rows, and stops for manual review rather than selecting a claimant. It then recreates
the three required partial indexes.

The ownership migration adds the durable single-manager token, idempotent payment-batch ledger and
recovery-file import manifest. Schema preflight resolves the unqualified `blocks` relation selected
by the application role's active `search_path`; unrelated same-named indexes cannot satisfy it.

Every merged-mining sender, direct recorder, relay receiver/recorder and database-connected payout
node refuses to continue when the required schema is absent or malformed.

## Relay and payout ownership

### Relay database boundary

Every merged-mining relay sender needs access to the shared PostgreSQL database and merged-mining
indexes. It persists financially significant block-only records synchronously; ZeroMQ carries only
ordinary shares. The paired parent share keeps its sender timestamp through current receivers.

Upgrade every relay receiver before its sender or before enabling merged mining. Older receivers do
not understand timestamp preservation and can place a winning share after its effort boundary. A
database-free relay sender remains supported for non-merged pools.

### Payout ownership

The central receiver/recorder can be the sole reconciliation and payout owner, so senders need not
run a payout manager. Alternatively, one relay sender with payment processing explicitly enabled can
own it.

A database advisory lock rejects a second healthy manager. A durable ownership row also prevents
replacement after the lock session or process is lost. Miningcore clears ownership automatically
only after payout execution stops with no active or unknown wallet submission.

Cancellation, transport loss, malformed success, wallet success without a transaction ID,
post-submission persistence failure and shutdown timeout all retain the marker and stop payout
processing. Conclusive validation and configuration failures before submission release the active
operation normally. Alephium sweeps apply the same rule to every result; null entries and blank IDs
are financially uncertain.

Before manual recovery, prove the old process is dead, reconcile daemon wallet history and follow the
[guarded payout-manager ownership procedure](database.md#recover-payout-manager-ownership-safely).
Automatic or hot-standby failover is unsupported. Pending blocks remain locked through terminal
transition and balance credit, and known wallet transaction IDs enter the idempotent batch ledger
before balance resets commit.

## Failure and durability boundary

### Ordinary shares and block candidates

ZeroMQ PUB/SUB is not an acknowledged durable queue for ordinary shares. A disconnect can lose
in-flight statistical shares even though reconnect behavior is tested. Merged-mining candidates do
not share that window: the submitting manager waits for PostgreSQL block persistence before
returning.

A recognised retryable database failure uses the write-through recovery journal and can continue
once the candidate is safe. An unexpected database or application failure also attempts the journal,
then stops the cluster because the accounting pipeline is no longer trusted. If both targets fail,
Miningcore marks the shared process failed, stops sibling pools and exits non-zero.

### Shutdown persistence

Miningcore has a 45-second Generic Host shutdown budget. Candidate quiescence begins directly from
`ApplicationStopping`, before Kestrel can consume that shared budget. During quiescence, candidate
persistence skips normal 2/4/8-second retry delays, gives the active PostgreSQL operation five
seconds, then force-flushes the recovery journal without inheriting miner or host cancellation.

A late PostgreSQL completion can overlap journal replay. Each synchronous block-only type must
therefore have a stable identity backed by a unique index and matching repository `ON CONFLICT`
rule. The direct path currently permits only:

- `auxpow`
- `auxpow-claim`
- `merged-parent`
- `merged-parent-uncertain`

Miningcore rejects undeclared future types before database submission. The hosted recorder,
`ShareRecorder` and `IBlockCandidateRecorder` resolve to one singleton, and journal writes are also
serialized by canonical recovery filename.

### Process and deployment behavior

Fatal startup, candidate durability failure, payout uncertainty and payout-ownership loss mark the
process failed before stopping. Any exception escaping shutdown, including host timeout, also returns
non-zero; a timely deliberate stop remains successful.

The supervisor must allow more than 45 seconds before forced termination. The supplied systemd unit
uses 90 seconds and `Restart=on-failure`. All participating nodes must use the intended shared
database, with exactly one reconciliation and payout owner.

Physical relay-path validation is required only when production uses share relay. It is not a gate
for a direct single-node deployment.

## Pre-production validation

The automated suite verifies the AuxPoW byte layout, password parsing, old/new relay compatibility, bounded recovery hashing, independent task draining, Alephium sweep identities, manager-level reorganisations, and that an accepted auxiliary candidate creates a block without a synthetic share row. CI also launches PostgreSQL 17 to verify exact index definitions under custom schemas, reject expression/order/predicate mutations, and include delayed direct and relay winning shares in effort. It does not launch Litecoin Core or Dogecoin Core.

Live results for the reference Windows/WSL regtest environment are tracked in
[merged-mining-regtest-validation.md](merged-mining-regtest-validation.md). Items marked outstanding
there remain deployment gates rather than being implied by the automated suite.

Before enabling mainnet traffic, run a daemon-backed regtest with real `litecoind` and `dogecoind` processes:

1. Create and fund/mature regtest wallets on both nodes, then configure distinct enabled LTC and DOGE SOLO pools.
2. Connect a Scrypt miner only to the LTC Stratum port using `d=<difficulty>;doge=<address>`.
3. Advance only the Dogecoin tip and confirm Miningcore broadcasts a fresh combined job without waiting for the Litecoin tip to change.
4. Submit an auxiliary-only solution and confirm `submitauxblock` accepts it, proof attribution matches the active block's `auxpow.parentblock`, one pending DOGE block row is created, and no synthetic DOGE row is added to `shares`.
5. Interrupt or restart Dogecoin RPC before the `submitauxblock` response is received. Confirm an uncertain block row records the submitted parent header, survives, and later resolves only if the active block's `auxpow.parentblock` matches it.
6. Mature the DOGE coinbase and confirm the normal DOGE classifier credits and pays the password-supplied beneficiary.
7. Repeat with a parent-only solution and with a solution meeting both targets to confirm LTC and DOGE submissions run independently.
8. Trigger a same-height Dogecoin template refresh and confirm a clean Stratum job is broadcast without a false chain-height notification.
9. Repeat with Litecoin MWEB enabled and a normal transaction-bearing parent template.
10. Submit two different parent proofs for one DOGE child template and confirm only the proof matching the accepted `auxpow.parentblock` is credited.
11. Reorg a DOGE AuxPoW block out of the active chain and confirm `confirmations = -1` rows do not finalize or receive wallet-index grace.
12. Trigger a height-decreasing Litecoin reorganisation and confirm the freshly fetched lower-height template replaces the old job when its `previousblockhash` changes.
13. Exercise relay disconnect/reconnect and PostgreSQL duplicate insertion explicitly if those deployment modes will be used in production. On the final receiver host, run `bash scripts/regtest/validate-physical-relay.sh RELAY_HOST RELAY_PORT POOL_ID SENDER_SOURCE` with PostgreSQL environment variables set, then submit mining work through the physical sender. The script verifies both the TCP path and end-to-end ordinary-share persistence.
