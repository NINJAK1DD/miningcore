# Litecoin–Dogecoin merged mining

Miningcore can run Litecoin as the parent Scrypt pool and submit the same proof of work to Dogecoin
through AuxPoW.

| Task | Section |
| --- | --- |
| Configure pools and miner logins | [Requirements and miner login](#requirements-and-miner-login) |
| Understand share and block accounting | [Share and block accounting](#share-and-block-accounting) |
| Diagnose auxiliary address rejection | [Auxiliary-address policy](#auxiliary-address-policy) |
| Diagnose template fallback or timeouts | [Template refresh](#template-refresh) |
| Apply database migrations | [Database migrations](#database-migrations) |
| Deploy relays and payout ownership | [Relay and payout ownership](#relay-and-payout-ownership) |
| Validate before production | [Pre-production validation](#pre-production-validation) |

For incident-first routing, use [Troubleshooting](troubleshooting.md).

## Requirements and miner login

The required relationship is:

- Litecoin and Dogecoin are separate enabled pools with unique IDs.
- Each pool independently uses `SOLO`, `PPS`, `PROP` or `PPLNS`; mixed schemes are supported.
- Direct and relay receiver/recorder nodes set cluster-level `paymentProcessing.enabled` to `true`.
- Dogecoin supplies its daemon, wallet address, block classification and payout pipeline.
- Litecoin references that Dogecoin pool through `mergedMining`.
- Non-SOLO Dogecoin pools set `requireAuxAddress: true`.

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

The Dogecoin daemon mines rewards to the configured Dogecoin pool wallet. Miningcore records the
password-supplied Dogecoin address as the auxiliary beneficiary. SOLO pays that address when its
candidate matures; pooled schemes use the auxiliary projection in Dogecoin's own accounting history.

## Share and block accounting

### Share publication and candidate persistence

A new parent job is generated when either chain changes. Each submitted Scrypt proof is checked
against both targets.

After proof validation:

1. Miningcore publishes one authoritative envelope containing correlated Litecoin and Dogecoin
   accounting projections before either daemon submission.
2. Litecoin and Dogecoin submissions run independently.
3. Each accepted or transport-uncertain block is synchronously persisted as a block-only candidate
   when its own submission completes.
4. Block candidates do not wait for the ordinary five-second share batch or ZeroMQ relay.

A slow or failed peer-chain path therefore cannot suppress ordinary accounting or move it beyond
the parent effort boundary. The recorder commits both projections and any PPS liabilities in one
PostgreSQL transaction. A duplicate envelope is authenticated by its durable UUID and payload hash,
then suppressed. A conflicting replay stops instead of crediting one side. A replay after one pool
has independently pruned its settled projection is also suppressed safely: the durable group receipt
proves the original pair was committed atomically, and every still-retained row is re-authenticated.

The parent projection belongs to the Stratum username and the auxiliary projection belongs to the
validated password address. They retain one timestamp, worker, session, source and achieved proof,
but use their own pool ID, normalized assigned/actual difficulty, network difficulty, template
height and spendable template reward. PROP therefore reads only its pool's round, and PPLNS builds
each window from that pool's projected shares and network difficulty.

### PPS liability contract

PPS becomes a liability when the accepted share envelope and balance update commit—not when a block
is found. For assigned difficulty `d`, chain network difficulty `D`, spendable template reward `B`,
and positive reward-recipient fraction `f`, the exact calculated liability is `(1 - f) * d / D * B`,
following the PPS contract in Meni Rosenfeld's
[Analysis of Bitcoin Pooled Mining Reward Systems](https://arxiv.org/abs/1112.4980).
Miningcore records it at 24 decimal places, rounds the payable balance down to the database's 12
decimal places, and carries the remainder per pool and miner. Actual above-target luck does not
increase the credit.

Rejected, stale and orphaned blocks do not reverse PPS credits; confirmed blocks do not add them a
second time. This transfers variance, reorg, daemon, wallet, liquidity and insolvency risk to the
operator. Maintain a separately monitored reserve able to cover miner balances during an extended
unlucky period. Reward recipients remain chain-local and reduce the PPS miner basis before the
liability is created.

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

When `requireAuxAddress` is true:

- authorisation fails when the address is missing or Dogecoin rejects it;
- a process-local cache retains up to 4096 positively validated addresses;
- a reconnect with an exact cached address may continue through a temporary validation-RPC outage;
- a new or unseen address still fails closed; and
- changing the address requires reconnecting.

The cache is not persisted and is empty after restart. Keep the Dogecoin pool enabled so its normal
classifier, maturity checks and payout processor can handle auxiliary blocks.

When `requireAuxAddress` is false, a worker that omits `doge=` mines Litecoin only. This is allowed
only when Dogecoin is SOLO. Miningcore does not submit an otherwise qualifying DOGE candidate or
credit a fallback address. Pooled Dogecoin accounting refuses this configuration at startup because
accepted unattributed work would create an unowned liability.

### Pool configuration safeguards

Pool ordering does not matter because enabled coin templates are assigned before pool configuration.
Other safeguards are:

- `addressParameter` is trimmed, defaults to `doge` when blank, and cannot be `d` or contain `;` or
  `=`.
- A definitively invalid DOGE login uses the normal failed-login ban path.
- A temporary validation-RPC failure returns a server error without banning the miner, unless the
  exact address was already validated by this process.
- When several Dogecoin daemon endpoints are configured, Miningcore warns and uses the first.
  Configure one authoritative auxiliary endpoint rather than assuming failover.

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
Litecoin mining can continue; the first fallback logs `Auxiliary template update failed`. Recovery
is logged only after a fresh template is successfully installed or a fresh response reconfirms the
already-installed auxiliary identity.

Do not raise the timeout solely because one warning appears. Confirm recovery, then inspect warning
frequency, Dogecoin synchronization, CPU and storage pressure, RPC saturation, and correlation with
payout activity. Increase the setting modestly only when a healthy local daemon consistently needs
longer. A longer deadline can delay a fresh parent Litecoin job. The 500 ms default remains the
general recommendation; an operator-specific 1000 ms setting can be reasonable when measurements
support it.

Prometheus exposes the complete startup and refresh paths, including attempts that time out or are
cancelled:

- `miningcore_auxiliary_template_rpc_duration_seconds` records `createauxblock` duration by parent,
  auxiliary pool, `startup`/`refresh` phase and bounded outcome. Its `_count` series is the attempt
  count.
- `miningcore_auxiliary_template_fallback_total` counts entries into degraded cached-template
  operation by parent/auxiliary pair.
- `miningcore_auxiliary_template_available` is `1` when an installed job has a usable auxiliary
  template and `0` when no combined job can be constructed.
- `miningcore_auxiliary_template_degraded` is `1` while the parent uses a cached template from the
  named auxiliary pool; otherwise it is `0`.

Accounting metrics are bounded by configured pool IDs and fixed outcome/role values; no miner
address or per-share UUID is a label:

- `miningcore_share_accounting_batches_total{outcome}` distinguishes inserted groups from
  authenticated `replay_suppressed` groups.
- `miningcore_share_accounting_projections_total{pool,role,outcome}` distinguishes parent,
  auxiliary and direct single-chain projections.
- `miningcore_pps_share_credits_total{pool,outcome}` counts PPS liabilities and suppressed replays.
- `miningcore_pps_liability_coin_total{pool}` accumulates the exact 24-decimal calculated liability
  before the 12-decimal balance boundary.
- `miningcore_merged_mining_attribution_rejections_total{pool,aux_pool,reason}` counts missing,
  invalid and temporarily unverifiable auxiliary payout attribution.

Separate parent-pool labels prevent a healthy parent from clearing another parent's degraded state
when both reference the same auxiliary pool. Separate phase labels keep ten-second startup probes
out of recurring timeout analysis. Histogram buckets straddle the 500 ms default and extend through
the ten-second startup deadline.

State gauges are reasserted on each auxiliary poll refresh so a
transient telemetry processing failure self-heals, while the fallback counter increments only on a
new degraded episode. After an active merged job exists, parent stream events that merely reuse its
cached auxiliary template do not reassert the gauges; the next configured `blockRefreshInterval`
poll does. A missing or nonpositive interval defaults to 1000 ms, while an explicitly configured
positive interval is respected.

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
maintenance window. Apply all three scripts before enabling merged mining:

- `src/Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql`
- `src/Miningcore/Persistence/Postgres/Scripts/add_payout_manager_ownership.sql`
- `src/Miningcore/Persistence/Postgres/Scripts/add_share_accounting.sql`

The accounting migration is required for any PPS, PROP or PPLNS participant. An unchanged
SOLO/SOLO topology retains its established one-share wire/database record and does not require it.

The ownership migration is required for every payment-processing cluster in this release series,
including clusters without merged mining. It is also required for recorder/recovery-only deployments
that use the `-rs` importer.

### Migration guarantees

All migrations are transactional, so failed validation or index creation rolls back the changes.
The AuxPoW migration uses regular `CREATE INDEX` within its transaction. It resolves the schema of
the active `blocks` relation before dropping obsolete indexes, detects legacy uncertain or duplicate
merged-mining rows, and stops for manual review rather than selecting a claimant. It then recreates
the three required partial indexes.

The ownership migration adds the durable single-manager token, idempotent payment-batch ledger and
recovery-file import manifest. Schema preflight resolves the unqualified `blocks` relation selected
by the application role's active `search_path`; unrelated same-named indexes cannot satisfy it.

The share-accounting migration adds a unique `(poolid, accountingid)` projection identity, a
correlated-group manifest, the immutable PPS credit journal and a locked precision-remainder table.
Startup verifies column types/nullability, exact primary/foreign keys, checks, and the partial unique
index. A same-named but structurally different object does not satisfy preflight.

Every merged-mining sender, direct recorder, relay receiver/recorder and database-connected payout
node refuses to continue when the required schema is absent or malformed.

## Relay and payout ownership

### Relay database boundary

Every merged-mining relay sender needs access to the shared PostgreSQL database and merged-mining
block indexes. It persists financially significant block-only records synchronously. ZeroMQ carries
the one paired ordinary-accounting envelope, whose projections keep the sender timestamp. The
receiver commits the pair and PPS liability.

Upgrade and migrate every relay receiver before upgrading senders, then stop old senders before
enabling pooled merged mining or PPS. The accounting envelope uses a new wire-format discriminator;
an old receiver drops it as unsupported, while a new receiver rejects accounting fields carried in
a legacy frame. This fail-closed behavior prevents a partial pair but means a mixed-version rollout
can intentionally lose shares if the order is ignored. A database-free relay sender remains
supported for non-merged, non-PPS pools.

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

ZeroMQ PUB/SUB is not an acknowledged durable queue. A disconnect can lose an entire in-flight
accounting envelope, but cannot commit only one projection. Merged-mining candidates do not share
that window: the submitting manager waits for PostgreSQL block persistence before returning.

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

The automated suite covers:

- AuxPoW layout, password parsing and old/new relay compatibility;
- bounded recovery hashing and independent task draining;
- Alephium sweep identities and manager-level reorganisations; and
- correlated parent/auxiliary projections, independent payout vectors, PPS idempotency and accepted
  auxiliary candidates without duplicate publication.

CI also launches PostgreSQL 17 to verify exact indexes and PPS ledger contracts under custom
schemas, reject mutated constraints, expressions, order and predicates, and include delayed direct
and relay winning shares in effort. Checksum-pinned Litecoin Core 0.21.5.5 and Dogecoin Core 1.14.9
regtest processes mine rewards through maturity; all four supported schemes and a mixed pairing
then create balances from that daemon evidence.

Live results for the reference Windows/WSL regtest environment are tracked in
[merged-mining-regtest-validation.md](merged-mining-regtest-validation.md). Items marked outstanding
there remain deployment gates rather than being implied by the automated suite.

Before enabling mainnet traffic, run a daemon-backed regtest with real `litecoind` and `dogecoind`
processes:

1. Create and fund/mature regtest wallets on both nodes, then configure distinct enabled LTC and
   DOGE pools. Exercise SOLO, PPS, PROP, PPLNS and at least one mixed pairing.
2. Connect a Scrypt miner only to the LTC Stratum port using
   `d=<difficulty>;doge=<address>`.
3. Advance only the Dogecoin tip and confirm Miningcore broadcasts a fresh combined job without
   waiting for the Litecoin tip to change.
4. Submit an auxiliary-only solution. Confirm `submitauxblock` accepts it, attribution matches the
   active block's `auxpow.parentblock`, one pending DOGE row is created, and exactly one correlated
   DOGE projection is inserted.
5. Interrupt Dogecoin RPC before the `submitauxblock` response arrives. Confirm the uncertain row
   records the submitted parent header, survives, and resolves only when the active block's
   `auxpow.parentblock` matches.
6. Mature the DOGE coinbase and confirm the normal DOGE classifier credits the correct beneficiary.
   For PPS, confirm the balance existed before maturity and did not change because of maturity.
7. Repeat with a parent-only solution and a solution meeting both targets; confirm LTC and DOGE
   submissions run independently.
8. Trigger a same-height Dogecoin template refresh and confirm a clean Stratum job without a false
   chain-height notification.
9. Repeat with Litecoin MWEB enabled and a normal transaction-bearing parent template.
10. Submit two parent proofs for one DOGE child template and confirm only the proof matching the
    accepted `auxpow.parentblock` is credited.
11. Reorg a DOGE AuxPoW block out of the active chain and confirm `confirmations = -1` rows do not
    finalize or receive wallet-index grace.
12. Trigger a height-decreasing Litecoin reorganisation and confirm the fresh lower template
    replaces the old job when `previousblockhash` changes.
13. If production uses relays, exercise relay disconnect/reconnect and PostgreSQL duplicate
    insertion. On the final receiver, run
    `bash scripts/regtest/validate-physical-relay.sh RELAY_HOST RELAY_PORT POOL_ID SENDER_SOURCE`
    with PostgreSQL environment variables set, then submit through the physical sender. The script
    verifies both TCP reachability and end-to-end ordinary-share persistence.
