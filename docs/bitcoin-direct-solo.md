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

Startup verifies the exact column types and validated settlement constraint before accepting direct
work. Reapplying the additive, transactional migration repairs a missing or weakened named
constraint. Existing block rows remain `NULL` in the new fields and retain their original custodial
settlement path.

Do not discard historical pending SOLO balances or blocks when switching modes. Let old custodial
blocks mature and pay through their original wallet/balance lifecycle. New direct blocks are marked
`coinbase-direct`, reconciled by block RPC and can never be reclassified as conventional payout
liabilities after a restart or configuration rollback.

## Configure payout outputs

The base username supplied to `mining.authorize` must be a valid address for the daemon's active
Bitcoin network. A worker suffix remains optional:

```text
bc1q...miner-address.worker-name
```

Miningcore validates and snapshots the base address before sending work. Each destination gets a
bounded, immutable job projection with its own coinbase suffix, transaction ID and merkle root.
Reauthorization clears that connection's old job queue; it cannot redirect an already-announced
job. Another connection cannot resolve or submit that projection.

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

Startup/job construction rejects more than 64 positive recipients, percentages totaling 100% or more, non-positive residuals,
positive outputs below one satoshi, wrong-network addresses, duplicate recipient scripts and a miner
script that duplicates a recipient. The bundled BTC path also refuses direct mode if the template
would divert part of `coinbasevalue` into a non-Bitcoin consensus-owned output.

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

An accepted candidate is synchronously stored before the ordinary statistical share is admitted.
The audit record includes the block hash, coinbase transaction ID, gross value, miner script/value
and every direct recipient output. Confirmation uses `getblock` with transaction details; the daemon
wallet does not need to own or spend the miner or recipient outputs, and `txindex` is not required.

Pending, confirmed and orphaned states remain visible through the normal block API and
notifications. A confirmed direct block updates block state only: payout schemes, miner balances,
recipient balances and payment submission are bypassed. On a reorg the audit row becomes orphaned;
no balance reversal is necessary because Miningcore never credited one.

Back up PostgreSQL and the normal recovery/quarantine artifacts. Never import a quarantine file with
`-rs`. Direct candidate durability depends on the local PostgreSQL block writer, so share-relay
sender/receiver deployments are rejected in this first version.

## Rollback

To disable direct payout, stop new work, wait for accepted candidates to be durably visible, set
`soloCoinbasePayout` to `false`, and restart through the normal controlled procedure. Do not drop the
new columns or constraint: historical direct records need them for restart-safe reconciliation.
Direct blocks continue through their recorded direct lifecycle; pre-switch custodial blocks continue
through the wallet/balance lifecycle.

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
