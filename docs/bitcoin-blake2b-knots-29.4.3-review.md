# Knots 29.4.3 rolling maturity review and upgrade

Reviewed 30 October 2026 for issue #230. The current supported node is exactly
**29.4.3.knots20260508**, numeric RPC version `290403`, source commit
[`a7c4617524c334f542484bfef3919b9f12573840`](https://github.com/bitcoinknots/bitcoin/tree/a7c4617524c334f542484bfef3919b9f12573840).
Catalogue contract: `knots-29.4.3-header-v2`. Printable operator comments and
suffixes are accepted only within the reviewed Satoshi/Knots user-agent grammar.
There is no spoofing bypass or open version range. Mainnet and isolated regtest
are supported; this does not establish universal network adoption.

## Source contract

The released [comparison](https://github.com/bitcoinknots/bitcoin/compare/v29.4.2.knots20260508...v29.4.3.knots20260508)
preserves header-v2 serialization, BLAKE2b proofs, ASIC profiles, compact targets
and difficulty/ledger units. Historical 29.4.1 header-vector provenance and
29.4.2 test pins remain historical records, not current daemon recommendations.

| Reward and spending state | Consensus depth | Wallet/mempool depth |
| --- | --- | --- |
| Creation before 973440, creation-parent MTP below cutoff | 100 | 6480 |
| Creation 973440–979918; spending through 979919 inclusive | 6480 | 6480 |
| Creation 973440–979918; spending from 979920 | 100 | 6480 |
| Creation from 979919 with creation-parent MTP below 1819756800 | 25920 | 25920 |
| Creation-parent MTP at or above 1819756800 | 100 after historical consensus ends | 100 |

The cutoff is 1 September 2027 00:00 UTC. It is the **parent's median time past
at creation**, never wall clock, the reward block's timestamp/MTP or today's tip
MTP. Covered rewards retain their full depth after expiry. Unknown creation
evidence withholds classification, including when the wallet reports `generate`.
Upstream policy conservatively selects long maturity for unknown creation blocks.

Sources at the pinned commit: [chain parameters](https://github.com/bitcoinknots/bitcoin/blob/a7c4617524c334f542484bfef3919b9f12573840/src/kernel/chainparams.cpp),
[consensus/policy](https://github.com/bitcoinknots/bitcoin/blob/a7c4617524c334f542484bfef3919b9f12573840/src/consensus/tx_verify.cpp),
[wallet](https://github.com/bitcoinknots/bitcoin/blob/a7c4617524c334f542484bfef3919b9f12573840/src/wallet/wallet.cpp),
[reorg/mempool validation](https://github.com/bitcoinknots/bitcoin/blob/a7c4617524c334f542484bfef3919b9f12573840/src/validation.cpp).

Wallet RPC thresholds retain the extra confirmation: **6481, 25921 or 101**,
then the maximum with the operator's `minimumConfirmations`. `immature` always
stays pending. Progress is confirmations divided by that reward's threshold,
capped below one while immature. Remaining confirmations use that same threshold.
The API's `confirmationProgress` is neither a payment receipt nor proof of
liquid backing. Immature owned output details carry reward value even when the
top-level wallet amount is zero.

## RPC observations and branch evidence

Before next height 979919, deployment metadata contains the historical six-field
contract: `type: flagday`, `height: 973440`, `height_end: 979919`,
`coinbase_start_height: 973440`, `maturity: 6480`, and next-height `active`.
At tip **979918**, it switches to `height: 979919`,
`coinbase_start_height: 979919`, `maturity: 25920`,
`coinbase_expiry_time: 1819756800`, and `active: true`; `height_end` is absent.
Missing, mixed, mistyped or additional maturity fields are rejected.
Rolling `active` and GBT `long_coinbase_maturity` remain present after expiry.
They cannot unlock an individual reward.

Separate chain/deployment RPCs may observe ordinary advancement across this
transition. Miningcore permits at most six blocks of snapshot distance, verifies
hash/height and immutable ancestry, then rechecks the higher snapshot's active
membership. This proves that the two observations share a branch at the final
read; independent positive confirmations on incompatible branches cannot attest.
Identical snapshots require one header RPC; differing snapshots cost at most
eight. A changing, incompatible or distant snapshot withholds the cycle with
reason `ChainChanged (705)`, separately from `RpcUnavailable (701)` and contract
drift. These observations do not promise an atomic view of subsequent RPCs.
Startup chain attestation retries the complete identity/chain/deployment/header
snapshot up to three times, waiting one and two seconds, honoring cancellation.
Successful contradictory identity, chain or contract evidence fails immediately.
The process binds
the complete network schedule, including rolling constants, rather than binding
the transient historical/rolling RPC phase. Configuration or chain changes still
require a stop/reconcile/restart. Mining work is re-attested on the first poll
after a 30-second cache expires and following RPC outages; every GBT's rules are
checked against its own height. Operators must keep RPC local/authenticated.
RPC identity checks are compatibility gates, not cryptographic authentication.

Payout classification reads fresh wallet data and a fresh creation header every
cycle. On a cache miss it also reads the linked parent and the creation header
again. The latter detects a reorg during the parent RPC that leaves the parent
active while orphaning its child. Hash, height, active membership and parent MTP
must match before caching. The process-wide LRU holds at most 32,768 immutable
creation hash/height/parent/MTP entries across handler recreation. A warm pending
reward needs one header RPC instead of three. Confirmations, active membership,
wallet category, spendability and accounting decisions are never cached.
Conflicting cached links withhold classification; proven orphaning or confirmation
removes the entry. Process restart clears the cache. The effective confirmation
count remains the smaller of fresh header and wallet counts.
These are header RPCs, so pruning old block bodies does not require ordinary
maturity assumptions. Missing, conflicting or stale evidence retains unresolved
funds and uses the existing bounded reconciliation alert. Daemon identity and
the deployment contract are checked again before wallet payout.

## Release provenance

Published [manifest/signatures](https://bitcoinknots.org/files/29.x/29.4.3.knots20260508/):

| Artifact | SHA-256 |
| --- | --- |
| Linux x86-64 archive | SHA-256 `4c67f1139b668fc3b98e339dd16112c636b2c0f42c2294b98fb92d1d27943e24` |
| Released source archive | SHA-256 `f1613e4ec63b7a1d87e9eba10aed730195a08c732e8883c4215e2d4680b9ed83` |
| Windows pgpverifiable ZIP (manifest pin) | SHA-256 `3035ee70d108f6ff7fa76b99df9e261181da34a471b30946cf5a1e5e504ec709` |

The lab downloaded and checksum-verified the Linux and source archives. In an
isolated keyring, the manifest produced `VALIDSIG` for Luke Dashjr's codesigning
fingerprint **1A3E761F19D2CC7785C5502EA291A2C45D0C504A** and Mark H Russell's
fingerprint **1D5889CB9E0564C154E18BB512EC9519DB43CC27**. The public keys were
obtained independently of the download server from the previously pinned
[builder-key repository](https://github.com/bitcoinknots/guix.sigs/tree/b5a8daaab328ba1d6e2d4af1613d4b42e0878481/builder-keys).
Both fingerprints were also cross-checked against the independent keys.openpgp.org
distribution service. This checks key continuity across distribution channels;
it does not replace an operator's independent signer-trust decision.
The [release tag](https://github.com/bitcoinknots/bitcoin/releases/tag/v29.4.3.knots20260508)
also identifies Luke's signing key. Two additional manifest signatures had no
matching key in this isolated keyring; they are not claimed as verified. GPG's
aggregate exit code therefore includes unknown signers; require the exact
expected `VALIDSIG`, not just a substring saying a signature was good. The old
29.4.2 manifest's Chris Guida/bitcoinmechanic signatures do not authenticate this
new manifest. CI checks the exact reviewed binary/source hashes on every download
and cache hit. Operators must independently establish signer trust before using
these pins in production. The Windows artifact was not executed in this Linux lab.

<a id="stop-upgrade-reconcile-restart"></a>

## Stop, backup, upgrade, revalidate, reconcile, restart

1. Upgrade before **height 979919**. Use observed heights and validation state.
   Per the [release guidance](https://github.com/bitcoinknots/bitcoin/releases/tag/v29.4.3.knots20260508),
   29.4.2 validation can diverge from 980019 and its mining policy can produce
   incompatible spends from 986399. Dates estimated from block intervals are
   not activation checks. Stop admission, PPS price/admission changes, payout
   workers and automated wallet sweeps; drain Miningcore cleanly.
2. Record exact node/Miningcore versions, chain tip/hash, configuration and
   mempool. Back up PostgreSQL consistently, including shares, blocks, balances,
   accounting receipts, leases, payment attempts and uncertain broadcasts. Back
   up the wallet with its supported wallet backup RPC, configuration and node
   data using a clean shutdown or consistent snapshot. Verify restoreability.
   Record pending/orphaned/quarantined and already credited rewards separately.
3. Audit every unconfirmed payout and change chain. Record txids, raw signed
   transactions and owned outputs without broadcasting replacements. A newly
   locked input may evict a transaction and its descendants. Absence from the
   new mempool is not proof a payout never happened. Preserve uncertain-broadcast
   reconciliation and the original attempt identity; never pay it a second time
   merely because upgrade/revalidation removed it from the mempool.
4. Stop Knots completely. Verify release signatures against independently
   authenticated keys and check the selected archive's exact hash. Stage the
   matching Miningcore/catalogue and verified 29.4.3 daemon together. Never
   spoof version strings to restart an old Miningcore build.
5. Start the node with mining and payouts disabled. A late upgrade revalidates
   history from the applicable deployment. Let revalidation/indexing and wallet
   synchronization finish; inspect logs, chainwork, active tip, IBD and wallet
   scan state. Pruned nodes lacking required history may need resynchronization;
   do not bypass that requirement. Preserve the pre-upgrade backup.
6. Reconcile reward hashes, heights, coinbase txids, owned amounts, wallet
   categories, parent hashes/MTP and confirmations against the revalidated branch.
   Pending/orphaned rows can recover only through matching evidence. The bounded
   automatic orphan window is **51840 blocks**, twice the longest reviewed
   25920-block interval, with **64-row keyset batches** and cursor wrap. It covers
   a full lock plus another interval for outage/reconciliation without scanning
   all history in a cycle. Restart resets the cursor, not stored ownership or
   accounting receipts. Extremely old orphans, Confirmed and Quarantined rows
   require an explicit operator audit; a horizon cannot cover arbitrary downtime.
7. Audit already credited rewards even though automatic scans exclude them.
   Revalidation may remove their backing. Compare saved allocation receipts,
   shares, balances, payments, PPS liabilities and measured mature reserves.
   Do not delete, reverse, reprice or recreate accounting rows automatically.
   For older recovered PROP/PPLNS rewards, consumed share history or a later/equal
   confirmed allocation produces a durable quarantine. Restore original evidence
   in a separate audit environment before authorizing any corrective ledger
   transaction; deleting the quarantine flag is not a recovery procedure.
8. Check spendable reserves, original unconfirmed attempts and change before
   resuming. Start matching Miningcore with bounded reconciliation, review
   classifications and alerts, then resume admission/payouts according to the
   measured reserve policy. `generate`, expiry and a progress value of one alone
   are insufficient to mark a payment successful. Existing immutable row locks,
   payout ownership and receipt/idempotency checks remain in force.
9. Rollback means stop admission and payouts again. Preserve all new data and
   attempt identities. Do not run 29.4.2 on the live post-activation chain or
   restore an old database while retaining newer wallet payments. Investigate in
   an isolated copy; only resume a mutually compatible reviewed node/Miningcore
   pair after reconciling both ledgers and chain state. A binary downgrade cannot
   undo consensus history, payments or already booked liabilities.

For PPS, reserve for roughly **180 days of newly locked income**, plus block
variance, revalidation/outages, withdrawal demand, fees and emergency capital.
Immature rewards are not liquidity. Keep already booked PPS liabilities and use
measured mature spendable reserves; stop new admission if reserve policy cannot
be met. Miningcore does not supply an automatic solvency guarantee.

## DATUM issue #163 handoff

The node prerequisite for [#163](https://github.com/NINJAK1DD/miningcore/issues/163)
is now this exact 29.4.3 source/build/maturity contract. Independently templating
gateways need matching reviewed validation rules; equal tip hashes do not prove
compatible consensus or policy. No DATUM server, eligibility blacklist, Stratum
removal or pool/ticker/ledger migration is introduced here.

| Component | Research pin | Status |
| --- | --- | --- |
| Node | `a7c4617524c334f542484bfef3919b9f12573840`, 29.4.3 | Required node contract |
| CONVOY gateway | `69c61a95cb941c35138c30b8d738e789ad8cdac1` | Source research; live interoperability remains #163 |
| Gateway user agent | `v0.5.0-beta` | Identification, not a stable wire version |

Review the pinned gateway's [protocol header](https://github.com/CONVOYMining/datum_gateway/blob/69c61a95cb941c35138c30b8d738e789ad8cdac1/src/datum_protocol.h)
for explicit four-byte serialization and DRS capability flags, its
[coinbaser](https://github.com/CONVOYMining/datum_gateway/blob/69c61a95cb941c35138c30b8d738e789ad8cdac1/src/datum_coinbaser.c)
for the 164-byte BLAKE2b header and output sigop/weight budgets, and
[difficulty configuration](https://github.com/CONVOYMining/datum_gateway/blob/69c61a95cb941c35138c30b8d738e789ad8cdac1/src/datum_conf.c)
for unit conversions. Later fixtures must negotiate supported/unsupported
extensions and preserve accepted-proof/credit identity. Future direct rewards
must distinguish allocation, inclusion, per-reward maturity, orphaning,
spendability and deferred wallet settlement, with exactly one payment route.
End-to-end gateway/server/direct-reward testing remains required in #163.

## Reproducible validation

### Compatibility hardening and regression coverage

The implementation includes the following compatibility, reliability and
operational protections, with regression evidence for each:

| Area | Behavior and regression evidence |
| --- | --- |
| Rolling regtest pre-start policy | Explicit `30:134:140` rolling schedules use 110 policy depth before height 30, ordinary consensus depth 100, and 111 wallet confirmations. Live height-29 checks cover 100/101/110/111 confirmations and API progress. |
| Record identity encodes consensus rules | Explicit historical start/release and rolling enforcement metadata replace `this == Mainnet`. Process binding compares the complete schedule. |
| Masked real-RPC errors | Every injected fault starts with a clean successful real-RpcClient attestation, including a real transported header response, and recovers afterward. No wallet submission is involved. |
| Transient startup failure | Bounded fresh-snapshot retries cover one-shot header orphaning, RPC failure and timeout; persistent failure, cancellation and successful contradictions have separate tests. |
| Sequential snapshot races | A six-block distance limit, verified ancestry and final active observation reject incompatible branches and mid-verification reorgs while allowing boundary advancement. Race alerts use reason 705. |
| Repeated creation RPC load | Bounded immutable creation-evidence cache survives handler recreation. Tests cover LRU eviction, pool separation, conflicting links, fresh wallet/header evidence, orphan removal and a cold-lookup reorg. The cold final read is retained because that interleaving can invalidate the child while leaving the parent active. |
| Dense orphan recovery | A 4,099-row test proves bounded pages, complete visitation and cursor wrap. Pool-scoped scan metrics and a read-only operator audit expose progress and horizon risk. |
| Post-cutoff recovery | Live Knots fixture restarts the daemon, rescans the wallet, replaces the branch, and reclassifies the original orphan and a mature replacement. |
| Upgrade payment uncertainty | Live Knots/PostgreSQL checks distinguish successful and wallet-only uncertain attempts. Mempool loss, restart, wallet rescan and original-transaction rebroadcast retain the original durable payment identity and produce one submission/debit/receipt. Existing durable payout-lease tests also retain ownership on uncertain outcomes. |
| Lab and CI issues | Both pinned gate versions remain linted and share one offline test implementation. Numeric PostgreSQL version selection, missing binaries, bounded console diagnostics and success are tested. Expiry must exceed regtest genesis time `1296688602`, matching upstream. |

### Recovery monitoring

Each completed bounded scan logs rows, cursor, minimum eligible height, oldest
batch height, near-horizon batch rows and sweep completion. Prometheus exposes
the same progress with only a configured pool label:

- `miningcore_blake2b_orphan_rows_scanned_total`: row visits, including repeated sweeps; use its rate for throughput.
- `miningcore_blake2b_orphan_sweeps_total`: completed keyset sweeps. Compare sweep time with the remaining horizon margin.
- `miningcore_blake2b_orphan_cursor`: last visited ID, including the first page after wrap.
- `miningcore_blake2b_orphan_batch_oldest_height`: minimum height in the last batch, zero when empty.
- `miningcore_blake2b_orphan_batch_near_horizon_rows`: last-batch rows within 2,592 blocks of the lower recovery boundary (about 18 days at target spacing).

These are scan observations, not global backlog counts, recovered rewards or
settlements. For the oldest eligible *remaining* row and full-window counts, run
the explicit [read-only audit](../scripts/ops/audit_blake2b_reconciliation.sql)
against the intended database with the verified current tip and logged cursor:

```bash
psql -X -v ON_ERROR_STOP=1 -v pool_id=POOL -v tip_height=HEIGHT -v cursor=ID \
  -f scripts/ops/audit_blake2b_reconciliation.sql
```

The audit uses a read-only transaction and a five-second statement timeout; it
does not run in the payout loop. Large databases may need an operator-planned
maintenance query if the timeout is exceeded. Use the audit's oldest remaining
height and remaining count with measured row throughput to detect a sweep that
cannot finish before rows leave the 51,840-block window. ID order is not height
order. Increasing payout frequency must respect daemon/database capacity; do not
raise limits blindly. Very old and already credited rows still require the
separate financial audit in the upgrade procedure.

Use the Ubuntu WSL distribution `Ubuntu` (26.04.1), ordinary user `ubuntu`, with independent
Linux Git metadata, loopback PostgreSQL and temporary regtest data directories.
The new gate `scripts/regtest/validate-knots-29.4.3-maturity.sh` checksum-verifies
the source archive and all four unmodified upstream functional scripts, then
runs the published `test_bitcoin --run_test=coinbase_maturity_tests` suite.
Functional tests use isolated schedule overrides/mocktime; mainnet constants
are checked independently by upstream unit vectors and Miningcore boundary tests.
Never run the overrides against an operator node or wallet.

### Validation on 30 October 2026

The lab used Ubuntu 26.04.1, .NET SDK 10.0.112, PostgreSQL 18.6,
the verified Knots 29.4.3 binary above, ordinary Bitcoin Core 31.1,
Litecoin 0.21.5.8 and Dogecoin 1.14.9. Production native sources built with
zero warnings/errors and passed the warning audit. Final managed builds prepared
embedded schema resources and reused those unchanged, audited native libraries.
The combined test build also built the current dev branch's native floating-point
test helper; it completed with zero warnings/errors.

The final implementation, incorporating current dev
`f366acc877124a00f0207c1875db57a1422d7ec7`, passed the full suite:
**5208 passed, zero failed, one benchmark skipped**, in 12 minutes 53 seconds.
The run enabled
real Knots/Core/Litecoin/Dogecoin, PostgreSQL accounting and isolated TLS/payment
fixtures. It exercised production startup, real Stratum shares/candidates,
accepted custodial blocks, all four supported accounting schemes, wallet payout
broadcast/confirmation, immutable accounting receipts, orphan recovery,
PROP/PPLNS quarantine, existing PPS liabilities, outages and handler/manager
recreation. Mainnet boundary, creation-parent MTP and wallet-margin vectors ran
alongside the unchanged ASIC/header, target and other-family regressions.

The unmodified signed-release upstream scripts all passed:
`mempool_long_coinbase_maturity.py`, `feature_chainstate_revalidation.py`,
`feature_rolling_coinbase_maturity.py`, and `feature_rolling_coinbase_upgrade.py`.
The published `test_bitcoin` maturity suite passed all four cases, proving
mainnet constants separately from isolated schedule/mocktime overrides.
Both offline cache/failure gates passed (five tests each), as did all four
lab-helper tests, ShellCheck, Markdown link/anchor checks and immutable
workflow-action pin checks. The final run includes all regressions listed above,
historical and rolling-pruned live fixtures, production payout/accounting and
unchanged ASIC/header and other-family tests. The managed build completed with
zero warnings/errors, reusing the unchanged audited production native libraries.

Final reports are `miningcore/build/knots-29.4.3-lab/knots-review-full.trx` and
`miningcore/build/knots-29.4.3-lab/upstream-review.log` under
`/home/ubuntu/issue-230`; the final managed build log is
`/tmp/issue230-review-build.log` and full console output is
`/tmp/issue230-review-full.log`. Earlier development reports remain in that lab
for traceability. This records isolated lab validation, not live
mainnet settlement or certification of every binary platform or gateway.

Example lab invocation (the password below belongs only to the disposable local lab):

```bash
export MININGCORE_TEST_BLAKE2B_BITCOIND=/home/ubuntu/issue-230/dependencies/binary/bin/bitcoind
export MININGCORE_TEST_POSTGRES='Host=127.0.0.1;Port=55433;Username=miningcore;Password=miningcore-test;Database=miningcore_tests'
export MININGCORE_KNOTS_SOURCE_CACHE=/home/ubuntu/issue-230/dependencies
bash scripts/regtest/run-knots-29.4.3-lab.sh
```

The runner requires an ordinary user, explicit reviewed Knots and PostgreSQL
inputs, and the lab's ordinary SHA256d/Litecoin/Dogecoin daemons under
`/opt/miningcore-test-lab/bin`. It builds `src`, audits warnings, then runs the
complete test suite. Reports are under `build/knots-29.4.3-lab`.
Automatic PostgreSQL selection compares numeric versions (`18` wins over `9.6`);
an explicit `MININGCORE_TEST_POSTGRES_BIN` takes precedence. An upstream-gate
failure prints its exit status, log path and last 200 lines while preserving the
complete log.
Port 55433 is a separate disposable PostgreSQL 18 cluster initialized for this
run under `/home/ubuntu/issue-230/postgres`, listening only on loopback. Its
fixture login is a database administrator, as in CI, because the suite exercises
administrative migrations. Do not elevate the shared lab or production
Miningcore runtime role. `MININGCORE_TEST_POSTGRES_BIN` enables the additional
isolated TLS, role-boundary and payment-persistence fixtures as an ordinary OS user.
On this machine, Windows managed worktree Git files use `R:` paths; Linux cannot
resolve them. Create independent Linux Git metadata by cloning the Windows
repository's common `.git` directory, select `origin/fix/knots-rolling-maturity`
as a local branch, then rsync source while excluding `.git`, `bin`, `obj` and
`build`. Never share build outputs or Windows Git metadata with the Linux test copy.
