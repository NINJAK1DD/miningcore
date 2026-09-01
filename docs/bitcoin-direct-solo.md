# Bitcoin direct-coinbase SOLO

Bitcoin direct-coinbase SOLO is an explicit, BTC-only mode that pays the block-finding miner in the
accepted block's coinbase transaction. Positive pool fee or donation recipients are separate
coinbase outputs. The miner reward never becomes a Miningcore balance and Miningcore never sends a
second payout transaction for that block.

This mode reduces post-block custody; it does not remove trust in the pool. A miner still trusts the
pool to construct the announced job honestly and submit a valid solution promptly. Coinbase outputs
also remain subject to Bitcoin's normal 100-block maturity rule.

Start from [`examples/bitcoin_direct_solo_pool.json`](../examples/bitcoin_direct_solo_pool.json).
Do not enable the option on an existing database until the migration and historical-liability checks
below are complete.

## Supported contract

The first implementation deliberately accepts only:

- the bundled canonical `bitcoin` template;
- Bitcoin mainnet, testnet or regtest;
- internal Stratum V1;
- pool-level and cluster-level payment processing enabled;
- `payoutScheme: "SOLO"`; and
- a direct, non-relay, non-merged-mining topology.

`soloCoinbasePayout` is a strict JSON Boolean and defaults to `false`. Unsupported families,
templates, networks, schemes, relay roles and merged-mining configurations fail before Stratum
listeners open. No existing SOLO pool changes behavior until the operator explicitly enables it.

## Database migration

Fresh databases created by a release containing this feature already have the required columns. For
an existing database, first follow the canonical
[candidate upgrade procedure](releases.md#upgrade-or-roll-back): extract and verify the new release
into an immutable candidate directory, stop every Miningcore database writer, and verify a backup.
Then run this migration from that candidate—not the active `/opt/miningcore` symlink:

```console
test -n "${MININGCORE_CANDIDATE_DIR:-}" &&
test -f "$MININGCORE_CANDIDATE_DIR/migrations/add_bitcoin_direct_solo.sql" &&
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f "$MININGCORE_CANDIDATE_DIR/migrations/add_bitcoin_direct_solo.sql"
```

Startup verifies the exact column types, combined settlement/submission-state constraint, dedicated
`bitcoin-coinbase-direct` candidate identity and unique index, ordered reconciliation index,
prepared-submission replay index, and both halves of the statement-scoped direct-row update guard
before accepting direct work. Reapplying the additive, transactional migration repairs a missing,
weakened or wrongly ordered named contract.
Existing block rows remain `NULL` in the new fields and retain their original custodial settlement
path. The database update guard is a last-resort protection against an older binary treating an
already-paid direct block as a conventional payout liability; it does not make binary downgrade a
supported operation.

Do not discard historical pending SOLO balances or blocks when switching modes. Let old custodial
blocks mature and pay through their original wallet/balance lifecycle. New direct blocks are marked
`coinbase-direct` with the distinct `bitcoin-coinbase-direct` block type, reconciled by block RPC
and can never be confused with the historical `bitcoin-direct` PPS candidate type or reclassified as conventional payout
liabilities after a restart or configuration rollback.

## Configure payout outputs

The base username supplied to `mining.authorize` must be a valid address for the daemon's active
Bitcoin network. A worker suffix remains optional:

```text
bc1q...miner-address.worker-name
```

Miningcore validates and snapshots the base address before sending work. Each destination gets a
bounded, immutable job projection with its own coinbase suffix, transaction ID and merkle root.
Reauthorization advances an immutable per-connection authorization generation and clears that
connection's old job queue. A concurrently building job from the previous generation cannot re-enter
the queue or be submitted. Submission and reauthorization share an ordered gate: an in-flight old
submission finishes before reauthorization can report success, while a reauthorization that wins
first makes the old generation unresolvable. An already-announced job cannot be redirected. Another
connection cannot resolve or submit that projection.

Configure direct fee/donation outputs with positive `rewardRecipients` entries. Delete the example
entry for a fee-free pool, or replace its `CHANGE_ME` address before startup:

```jsonc
"soloCoinbasePayout": true,
"rewardRecipients": [
  {
    "address": "REPLACE_WITH_POOL_FEE_ADDRESS",
    "percentage": 2
  }
]
```

Zero-percent entries create no output. For every positive percentage Miningcore calculates:

```text
recipient satoshis = floor(coinbasevalue × percentage / 100)
miner satoshis     = coinbasevalue − sum(recipient satoshis)
```

The calculation uses exact integer/rational arithmetic, not binary floating point. The miner is the
first value-bearing direct output. Positive recipients follow in canonical script order. The
existing witness commitment remains present and consensus-valid. The miner residual makes the
complete output total exactly equal to GBT `coinbasevalue`.

Each template update and rebroadcast builds a destination-specific coinbase and merkle projection
for every connected worker. That is intentionally proportional to connected workers multiplied by
the template transaction count; capacity-test large fleets before enabling this first-version SOLO
mode.

If a later daemon template or destination-specific coinbase violates the direct-coinbase or final
block-weight contract, Miningcore invalidates every announced direct job, raises an administrative
alert and enters the process-wide mining fail-stop. A supervised service may restart only after
startup can construct a valid template; it does not leave an online pool repeatedly disconnecting
workers or serving stale direct work.

Startup/job construction rejects more than 64 positive recipients, percentages totaling 100% or more, non-positive residuals,
positive outputs below one satoshi, wrong-network addresses, duplicate recipient scripts and a miner
script that duplicates a recipient. The bundled BTC path also refuses direct mode if the template
would divert part of `coinbasevalue` into a non-Bitcoin consensus-owned output. The count limit is a
resource bound, not a block-fit assumption: Miningcore parses every template transaction,
verifies its daemon-reported weight against the serialized witness and stripped forms, then combines
that total with each destination's exact serialized coinbase and block overhead. Work is never
announced if the final block would exceed Bitcoin's 4,000,000-weight-unit consensus limit; missing,
non-positive, malformed or mismatched transaction data fails the template closed.

The pool `address` remains configured for daemon startup checks and for historical custodial blocks.
It is not the destination of a new direct block unless the operator separately lists it as a
positive direct recipient.

## Preflight before production hashing

1. Apply the migration, verify the backup and start on regtest or a controlled test endpoint.
2. Authorize with a test-network address plus an optional worker suffix.
3. Capture the `mining.notify` message. Concatenate `coinb1 + extranonce1 + extranonce2 + coinb2`
   using an extranonce2 of the advertised size, then decode the resulting transaction.
4. Confirm the coinbase contains the expected miner script and each positive recipient script.
5. Recalculate every satoshi amount with the floor rule above and confirm the values sum exactly to
   the template's `coinbasevalue`.
6. Confirm the `6a24aa21a9ed...` witness-commitment output remains present.
7. Mine and submit a regtest block, mature it, and verify the block API reports
   `settlementMode: "coinbase-direct"`, the gross satoshis, miner satoshis and recipient outputs.
8. Confirm no miner balance, payment batch or wallet send was created for that direct block.

Current AxeOS derives the miner's displayed share by matching coinbase output scripts against the
address portion of its username. A correctly configured direct job therefore reports a non-zero
share matching the miner residual. Treat that display as interoperability evidence only; Bitcoin
Core block acceptance and exact on-chain scripts/amounts are authoritative.

## Confirmation, restart and reorg behavior

Every locally validated direct candidate crosses a durable submission-outbox boundary before its
block is submitted to the daemon and before the ordinary statistical share is admitted. The outbox
stores the exact serialized block, locally calculated block hash and coinbase transaction ID,
immutable settlement evidence, attempt counters and one of `prepared`, `submitted-uncertain`,
`observed-active` or `rejected`. A prepared row is not announced as found and cannot be classified
as orphaned merely because the daemon has not seen it yet.

The propagation-critical prepare step makes one bounded PostgreSQL attempt. If that attempt fails,
Miningcore immediately fsyncs the same complete record to the protected recovery journal instead of
waiting through the ordinary 2/4/8-second database retry ladder. If neither store can durably retain
the exact block, mining fail-stops and the block is not submitted without recoverable evidence. An
unexpected non-retryable database error also schedules a fail-stop, but only after the already
journaled block has had its daemon-submission opportunity.

Startup replays `prepared` and `submitted-uncertain` rows from the exact stored bytes before opening
Stratum. Recovery import recreates the same replayable row; payout reconciliation provides an
additional idempotent replay path. Bitcoin Core duplicate handling makes repeated `submitblock`
safer than losing a possibly unsubmitted solution. Only an exact active-chain block/coinbase match
transitions to `observed-active` and emits the block-found notification. Ambiguous outcomes stay
pending. A definitive rejection requires at least three definitive misses over at least 30 minutes
before the outbox becomes `rejected`; a later exact active-chain observation can reactivate it.

The audit record includes the exact block, block hash, coinbase transaction ID, gross value, miner
script/value and every direct recipient output. Confirmation uses `getblock` with transaction
details; the daemon wallet does not need to own or spend the miner or recipient outputs, and
`txindex` is not required.

Pending, confirmed, orphaned and quarantined states remain visible through the normal block API. A
confirmed direct block updates block state only: payout schemes, miner balances, recipient balances
and payment submission are bypassed. `confirmed` means the configured confirmation threshold has
been reached; the miner output remains subject to Bitcoin's independent 100-block coinbase-maturity
rule and may not yet be spendable at that instant.

Confirmed and orphaned direct rows within 4,032 blocks of the reported chain tip remain in a bounded,
restart-safe reconciliation rotation: each row is rechecked no more than once per hour, and the
persisted last-check time prevents an old prefix from starving later rows. Pending rows are always
classified regardless of depth. On a post-maturity reorg inside that deliberately conservative
two-difficulty-period window the audit row becomes orphaned; if that exact block later returns to the
active chain, immutable evidence permits it to return to pending or confirmed. A deeper chain rewrite
is outside automatic reconciliation and requires an operator audit of the immutable settlement rows.
No balance reversal or recreation occurs because Miningcore never credited one. A malformed or
internally inconsistent historical direct row is quarantined individually, stamped out of the scan
prefix and excluded from financial settlement so it cannot stop unrelated pool payments; investigate
the database evidence before any manual change. Ordinary confirmed pool blocks remain terminal and
are not admitted to this direct-only path.

Back up PostgreSQL and the normal recovery/quarantine artifacts. Never import a quarantine file with
`-rs`. Direct candidate durability depends on the local PostgreSQL block writer, so share-relay
sender/receiver deployments are rejected in this first version. A direct candidate written to the
recovery journal uses the dedicated `bitcoin-coinbase-direct` identity and includes the exact
serialized block plus submission state. Recovery refuses import unless the complete direct-SOLO
outbox schema contract is present. Binaries predating this feature do not recognize that identity
and fail closed instead of importing it as a custodial block.

Before upgrading from any pre-release build of this feature, stop every writer and drain or manually
reconcile its recovery journal. Earlier draft builds wrote direct evidence under the historical
`bitcoin-direct` identity; later pre-release drafts could omit the exact serialized block and
submission state. Current recovery deliberately quarantines either ambiguous shape rather than
inventing a resubmittable payload. Do not pass the resulting quarantine file to `-rs`.

## Rollback

To disable direct payout, stop new work, wait for accepted candidates to be durably visible, set
`soloCoinbasePayout` to `false`, and restart through the normal controlled procedure. Do not drop the
new columns or constraint: historical direct records need them for restart-safe reconciliation.
Direct blocks continue through their recorded direct lifecycle; pre-switch custodial blocks continue
through the wallet/balance lifecycle.

Application rollback across this feature boundary is **not supported after direct work has been
accepted**. Database rows are only one evidence source. Before considering an older binary, stop
every writer and inspect both PostgreSQL and every configured recovery, emergency-journal and
quarantine path. This query is necessary but not sufficient:

```sql
SELECT count(*) AS direct_settlement_rows
FROM blocks
WHERE settlementmode = 'coinbase-direct';
```

If the result is non-zero, or any outstanding journal/quarantine evidence may contain an accepted
direct candidate, do not run a Miningcore binary that predates Bitcoin direct-coinbase SOLO.
A zero row count does not prove rollback safety after a database-write failure because the only
durable candidate may still be in the journal.
Such a binary does not understand that the coinbase already paid the miner and could attempt a second
wallet/balance settlement or corrupt the audit status. Keep the additive schema and update guard,
deploy a compatible fixed binary, or restore a verified pre-feature database in an isolated recovery
environment and reconcile every post-backup financial event before redirecting production traffic.
The database guard applies to all updates of a direct row; an old payout manager that encounters one
can abort that pool's complete payment cycle. This is deliberate fail-closed behavior, not a
supported mixed-version or downgrade mode.

If migration or startup validation fails, leave the old release symlink unchanged and keep all
Miningcore writers stopped until the database state is understood. Follow the
[release rollback boundary](releases.md#upgrade-or-roll-back) rather than attempting to reverse a
partially committed production history.

## Source-verified design references

- [CKPool SOLO behavior at `c26eb7f`](https://github.com/ckolivas/ckpool/blob/c26eb7ff2df5535982dcfb80bafe1bab346eaf34/README-SOLOMINING.md)
  and its [per-user work construction](https://github.com/ckolivas/ckpool/blob/c26eb7ff2df5535982dcfb80bafe1bab346eaf34/src/stratifier.c).
- [AxeOS coinbase decoder at `3a09ea0`](https://github.com/bitaxeorg/ESP-Miner/blob/3a09ea00c6f1254e4e19cb7033f8f6b8bf055e44/components/stratum/coinbase_decoder.c)
  and its [dashboard percentage handling](https://github.com/bitaxeorg/ESP-Miner/blob/3a09ea00c6f1254e4e19cb7033f8f6b8bf055e44/main/http_server/axe-os/src/app/components/home/home.component.ts).
- [BIP 22 `coinbasevalue`](https://github.com/bitcoin/bips/blob/master/bip-0022.mediawiki),
  [BIP 34 coinbase height](https://github.com/bitcoin/bips/blob/master/bip-0034.mediawiki), and
  [BIP 141 witness commitment](https://github.com/bitcoin/bips/blob/master/bip-0141.mediawiki#commitment-structure).
