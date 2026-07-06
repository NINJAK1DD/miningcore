# Litecoin–Dogecoin merged mining

Miningcore can run Litecoin as the parent Scrypt pool and submit the same proof of work to Dogecoin through AuxPoW.

This version is limited to SOLO. Configure Litecoin and Dogecoin as separate enabled pools with unique pool IDs, both using `SOLO` payment processing. The Dogecoin pool supplies its daemon, wallet address, block classification and payout pipeline. The Litecoin pool references it:

```json
"mergedMining": {
  "enabled": true,
  "auxPoolId": "doge-solo",
  "addressParameter": "doge",
  "requireAuxAddress": true
}
```

Connect miners only to the Litecoin Stratum endpoint.

- Username: `LTC_ADDRESS.worker`
- Password: `d=65536;doge=DOGE_ADDRESS`

The Dogecoin daemon mines rewards to the configured Dogecoin pool wallet. Miningcore records the password-supplied Dogecoin address as the SOLO beneficiary and pays it through the existing Dogecoin payout processor after maturity.

A new parent job is generated when either chain changes. Each submitted Scrypt proof is checked against both targets. Litecoin and Dogecoin block submissions are independent. Accepted or transport-uncertain merged-mining blocks are sent through the share message pipeline as block-only candidates as soon as their own submission finishes; only block-only records bypass the ordinary five-second share batch. The original Litecoin share retains normal stream ordering, and no synthetic Dogecoin share row is inserted. If acceptance or the coinbase transaction is not yet available, the block stores a reconciliation marker; the payout classifier retries `getblock` and replaces that marker with the coinbase transaction ID before monitoring maturity. An uncertain DOGE claim also records the submitted parent header and is credited only when it matches `auxpow.parentblock` from the accepted DOGE block. Multiple claims may coexist for one DOGE child hash, while only the matching proof can become the finalized AuxPoW row. Uncertain submissions require at least three definitive misses and 30 minutes before expiring as orphaned.

When `requireAuxAddress` is true, authorisation fails if the address is missing or rejected by Dogecoin's `validateaddress` RPC. The Dogecoin pool must remain enabled so its normal classifier, maturity checks and payout processor can handle auxiliary blocks.

All enabled pool coin templates are assigned before any pool is configured, so the LTC and DOGE entries may appear in either order. `addressParameter` is trimmed, defaults to `doge` when blank, and cannot be `d` or contain `;` or `=`. Definitively invalid DOGE logins use the normal failed-login ban path; a temporary DOGE validation RPC failure returns a server error without banning the miner.

The parent pool polls both templates even when a Bitcoin Template Stream is configured, because parent-chain notifications do not include Dogecoin tip changes. Stream events are treated as refresh signals rather than authoritative snapshots, and lower-height parent templates are ignored. After the initial combined job, a temporary DOGE template outage uses the last valid auxiliary template so fresh LTC jobs continue. Startup, recurring template polling, address validation, submission and ambiguity lookup use separate timeouts. If either submission has a transport-ambiguous result, Miningcore checks `getblock` and persists an uncertain marker when the daemon is still unavailable; explicit Dogecoin JSON-RPC errors are rejected without creating a candidate. A resolved AuxPoW row is not orphaned solely because the wallet temporarily returns `gettransaction -5` while the DOGE child block is still present.

For an existing PostgreSQL database, apply `src/Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql` before enabling merged mining. It checks for legacy uncertain or duplicate AuxPoW rows and stops for manual review rather than choosing a claimant automatically, then adds partial indexes for finalized blocks and exact claim deduplication.

In share-relay deployments, upgrade every pool, relay, receiver, and recorder node together before enabling merged mining because older nodes do not understand the protobuf `BlockOnly` and `BlockRecordEmitted` fields. The current ZeroMQ PUB/SUB relay is not an acknowledged durable queue: a block event can be lost while the receiver is disconnected. For financially durable production operation, run the recorder in the same process as the merged-mining pool or put an acknowledged durable transport/outbox in front of the recorder; coordinated upgrades alone do not remove this delivery risk.

## Pre-production validation

The automated suite verifies the AuxPoW byte layout, password parsing, relay compatibility, and that an accepted auxiliary candidate creates a block without a synthetic share row. It does not launch Litecoin Core or Dogecoin Core.

Before enabling mainnet traffic, run a daemon-backed regtest with real `litecoind` and `dogecoind` processes:

1. Create and fund/mature regtest wallets on both nodes, then configure distinct enabled LTC and DOGE SOLO pools.
2. Connect a Scrypt miner only to the LTC Stratum port using `d=<difficulty>;doge=<address>`.
3. Advance only the Dogecoin tip and confirm Miningcore broadcasts a fresh combined job without waiting for the Litecoin tip to change.
4. Submit an auxiliary-only solution and confirm `submitauxblock` accepts it, one pending DOGE block row is created, and no synthetic DOGE row is added to `shares`.
5. Interrupt or restart Dogecoin RPC before the `submitauxblock` response is received. Confirm an uncertain block row records the submitted parent header, survives, and later resolves only if `auxpow.parentblock` matches it.
6. Mature the DOGE coinbase and confirm the normal DOGE classifier credits and pays the password-supplied beneficiary.
7. Repeat with a parent-only solution and with a solution meeting both targets to confirm LTC and DOGE submissions run independently.
8. Trigger a same-height Dogecoin template refresh and confirm a clean Stratum job is broadcast without a false chain-height notification.
9. Repeat with Litecoin MWEB enabled and a normal transaction-bearing parent template.
10. Submit two different parent proofs for one DOGE child template and confirm only the proof matching the accepted `auxpow.parentblock` is credited.
11. Exercise relay disconnect/reconnect and PostgreSQL duplicate insertion explicitly if those deployment modes will be used in production.
