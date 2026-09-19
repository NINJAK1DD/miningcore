# PPS arithmetic migration

PPS arithmetic version 0 preserves Miningcore's historical binary64-to-decimal
conversion and decimal division. Version 1 computes the exact binary64 rational
ratio, integer satoshi reward and decimal retained percentage, truncating only
the final result to 24 places (CoreDRP PPSLiabilityV1 arithmetic).

The 19 September 2026 real-service regtest case used assigned difficulty `1e-10`,
network difficulty `4.65e-10`, 50 test BTC reward and 99% miner retention. Version 0
produces `10.64516129032258064516129`; version 1 produces
`10.645161290322581103779573`. Across 40 shares the difference is
`0.00000000000001834473132` test BTC.
Backup SHA-256: `910f2fa0743c021533ac866efc3f241d4b8a5b0d5de7624f119d0530cd7ad411`.
These numbers are regression evidence, not production exposure estimates.

## Explicit, one-way activation

Use an isolated restored database first. Stop all producers, relays, recorders
and payout writers. Drain journals with the accepting legacy configuration;
retain backups, receipt hashes, old liabilities and all fractional remainders.
Upgrade every hop before opting in: old binaries do not understand the new relay
field. Mixed-version operation after activation is unsupported.

Apply `src/Miningcore/Persistence/Postgres/Scripts/add_pps_arithmetic_version.sql`
with the database administrator role. New installs and the cumulative
`add_share_accounting.sql` upgrade include the same additive migration. Existing
credits get version 0 metadata; no amounts, hashes, balances or remainders change.

Choose a future UTC cutoff after all retained/pending legacy events, then call
this administrative function (replace the illustrative scope and timestamp):

```sql
SELECT activate_pps_binary64('bitcoin-pps-lab', '2026-09-21T00:00:00Z');
```

The function serializes against credit inserts, rejects a cutoff overlapping
existing credits, and records one immutable per-pool transition. Repeating the
same request is harmless; changing or deleting the boundary is rejected. The
activation function is not executable by PUBLIC. Use the function owner/admin;
do not insert transition rows manually. No user-facing command runs a migration
or activates a pool implicitly.

Set the identical cutoff in that pool's accepting/recording configuration:

```json
"paymentProcessing": {
  "enabled": true,
  "payoutScheme": "PPS",
  "ppsBinary64Activation": "2026-09-21T00:00:00Z"
}
```

This is a fragment: preserve the existing minimum payment, fee recipients and
other settings. Do not combine this rollout with a fee-policy change. An omitted
cutoff keeps legacy arithmetic. UTC share time selects version 0 before the
cutoff and version 1 at/after it. The database insert guard independently checks
that choice; missing/mismatched configuration fails insertion instead of mixing
contracts. Monitor that guard on startup before returning the pool to service.

## Replay and remainder preservation

The arithmetic version is immutable evidence in the share relay (new protobuf
field 33), JSON journals, PPS credit record, and version-1 receipt hash. Missing
historical fields mean version 0; version-0 hash bytes are unchanged. Existing
receipt checks run before any new ledger effect. An already committed legacy
replay stays legacy after activation. A changed version on replay conflicts even
when the numeric amount happens to agree. Sanitized recovery preserves the
embedded amount and version, with a version-appropriate exact zero-fee ceiling.

The same recipient remainder row is carried forward. A version-1 credit adds to
its existing fractional amount in the existing atomic ledger transaction. It
never resets or recalculates old fractional liabilities. To revert operationally,
stop new admission and reconcile; do not deploy an old binary, move the cutoff,
remove the trigger, or rewrite old credits to force compatibility.

## Limits and tests

The current CLR decimal transport cannot represent every NUMERIC(38,24) value.
Version 1 rejects a result that cannot fit **exactly**, including underflow to
zero and overflow, before admission. It never uses a tolerance or a second
rounding operation. Supporting all 38 digits needs a separate amount-transport
upgrade. This change adopts an arithmetic contract only; it does not advertise
full CoreDRP Mining conformance or provide policy/clock/completeness proofs.

Unit tests freeze the reported legacy/exact values, UTC boundary, wire/journal
round trips, unknown versions, sanitized recovery and extreme binary64 ratios.
PostgreSQL tests cover one-way activation, overlap rejection, old replay after
cutover, replay-version conflict, stale-writer rejection and nonzero remainder
continuity with exactly one durable credit per accounting identity.
