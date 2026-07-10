# Litecoin–Dogecoin merged mining

Miningcore can run Litecoin as the parent Scrypt pool and submit the same proof of work to Dogecoin through AuxPoW.

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

A new parent job is generated when either chain changes. Each submitted Scrypt proof is checked against both targets. Litecoin and Dogecoin block submissions are independent. Accepted or transport-uncertain merged-mining blocks are sent through the share message pipeline as block-only candidates as soon as their own submission finishes; only block-only records bypass the ordinary five-second share batch. The original Litecoin share retains normal stream ordering, and no synthetic Dogecoin share row is inserted.

Merged Litecoin parent rows use explicit block types: `merged-parent` once accepted and `merged-parent-uncertain` while an ambiguous parent submission is waiting for reconciliation. Dogecoin rows use `auxpow` once accepted and `auxpow-claim` while an ambiguous proof-specific claim is unresolved. Uncertain rows are deliberately excluded from public block totals, last-block timestamps and block-effort boundaries until they resolve, and they do not emit normal block-found notifications. If a claim later promotes to an accepted block, the normal block-found notification is emitted only after its database transaction commits. Losing, expired or superseded claims do not emit the ordinary orphan notification because they were never announced as found blocks.

If acceptance or the coinbase transaction is not yet available, the block stores a reconciliation marker; the payout classifier retries `getblock` and replaces that marker with the coinbase transaction ID before monitoring maturity. Dogecoin proof attribution is required for both transport-ambiguous and Boolean `submitauxblock: true` responses. A DOGE candidate is finalized only when the active child block's `auxpow.parentblock` matches the submitted parent header. Missing proof data creates a proof-specific claim instead of trusting the Boolean response; a different parent proof means this miner lost. Finding the child hash alone is not sufficient. Dogecoin blocks with `confirmations = -1` are treated as inactive/orphaned, not as payable accepted blocks. Multiple claims may coexist for one DOGE child hash, while only the matching proof can become the finalized AuxPoW row. Uncertain submissions require at least three definitive misses and 30 minutes before expiring as orphaned; active DOGE responses that repeatedly omit `auxpow.parentblock` use the same retry/expiry guard instead of remaining pending forever. An exact active block is never classified as orphaned merely because the daemon or an RPC proxy temporarily omits the transaction list; coinbase lookup remains pending until the txid becomes available or the block becomes inactive/definitively absent.

When `requireAuxAddress` is true, authorisation fails if the address is missing or rejected by Dogecoin's `validateaddress` RPC. The Dogecoin pool must remain enabled so its normal classifier, maturity checks and payout processor can handle auxiliary blocks.

All enabled pool coin templates are assigned before any pool is configured, so the LTC and DOGE entries may appear in either order. `addressParameter` is trimmed, defaults to `doge` when blank, and cannot be `d` or contain `;` or `=`. Definitively invalid DOGE logins use the normal failed-login ban path; a temporary DOGE validation RPC failure returns a server error without banning the miner.

The parent pool polls both templates even when a Bitcoin Template Stream is configured, because parent-chain notifications do not include Dogecoin tip changes. Stream events are treated as refresh signals rather than authoritative snapshots. A freshly fetched Litecoin template with a different `previousblockhash` is accepted as a new job even if its height is lower than the previous job, because a height-decreasing active-chain reorganisation is valid. The successful startup Dogecoin template is cached and can seed the first combined job if the first recurring refresh is slower than the normal poll timeout. After the initial combined job, a temporary DOGE template outage uses the last valid auxiliary template so fresh LTC jobs continue. Startup, recurring template polling, address validation, submission and ambiguity lookup use separate timeout caps; `auxiliaryTemplatePollTimeoutMs` controls recurring Dogecoin `createauxblock` and defaults to 500 ms. Submission and its follow-up attribution lookup share one ten-second operation deadline, so an outage cannot extend a block-candidate Stratum response to ten seconds plus a second lookup timeout. Litecoin parent submissions that return JSON null, `inconclusive`, `duplicate` or `duplicate-inconclusive` are reconciled with `getblock`; if the parent block cannot yet be proven inactive and its coinbase transaction is unavailable, Miningcore persists `merged-parent-uncertain` rather than discarding the candidate. If either submission has a transport-ambiguous result, Miningcore checks `getblock` and persists an uncertain marker when the daemon is still unavailable; explicit Dogecoin JSON-RPC errors are rejected without creating a candidate. A resolved AuxPoW or merged-parent row is not orphaned solely because the wallet temporarily returns `gettransaction -5` while the child or parent block cannot be proven inactive. If the active-chain check itself remains continuously unavailable for the uncertain-block lifetime, Miningcore emits one admin notification for that unavailable episode so operators can inspect wallet indexing, `getblock` and any RPC proxy. A successful wallet response or active/inactive block lookup clears the episode.

For an existing PostgreSQL database, stop Miningcore block writers or schedule a maintenance window, then apply `src/Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql` before enabling merged mining. The complete migration is transactional: failed validation or index creation rolls back its row changes and index operations. It uses regular `CREATE INDEX` inside that transaction rather than concurrent index builds. It checks for legacy uncertain or duplicate AuxPoW/merged-parent rows and stops for manual review rather than choosing a claimant automatically, then recreates all three required partial indexes so stale prerelease definitions are repaired. Direct, relay receiver/recorder and database-connected relay payout nodes refuse to continue if those indexes are missing or are not unique, valid, ready indexes with the expected definitions.

In share-relay deployments, upgrade every pool, relay, receiver, and recorder node together before enabling merged mining because older nodes do not understand the protobuf `BlockOnly` and `BlockRecordEmitted` fields. A database-free relay sender does not run a local schema check or payout manager. A relay sender with PostgreSQL and explicitly enabled payment processing may remain the single payout node; merged mining then applies the same schema preflight there. Otherwise the central receiver/recorder must own PostgreSQL, the required indexes and cluster-level payment processing. Run only one active payout manager for a database/pool set. Block-only relay records bypass ordinary share telemetry and network-stat updates on the receiver.

The current ZeroMQ PUB/SUB relay is not an acknowledged durable queue: a block event can be lost while the receiver is disconnected. Running the recorder in the same process reduces the loss window but still hands persistence to the asynchronous recorder pipeline. For financially durable production operation, write a synchronous repository/outbox record before returning from accepted submission or put an acknowledged durable transport in front of the recorder; coordinated upgrades alone do not remove this delivery risk.

## Pre-production validation

The automated suite verifies the AuxPoW byte layout, password parsing, relay compatibility, and that an accepted auxiliary candidate creates a block without a synthetic share row. It does not launch Litecoin Core or Dogecoin Core.

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
13. Exercise relay disconnect/reconnect and PostgreSQL duplicate insertion explicitly if those deployment modes will be used in production.
