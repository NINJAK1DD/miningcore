# Knots 29.4.2 compatibility review and upgrade

Reviewed on 2026-10-04 for [#205](https://github.com/NINJAK1DD/miningcore/issues/205),
following the exact-build policy in [#151](https://github.com/NINJAK1DD/miningcore/issues/151).
The accepted daemon is **29.4.2.knots20260508**, numeric version `290402`, with exact
subversion `/Satoshi:29.4.2/Knots:20260508/`. Protocol metadata is
`knots-29.4.2-header-v2`. Earlier 29.4.1 node guidance is historical provenance,
not an accepted current-mainnet build. There is no version bypass.

## Reviewed evidence

The [released source](https://github.com/bitcoinknots/bitcoin/tree/58398baf33e588779685ead478e6397bb28ed3d6)
and [29.4.1–29.4.2 source comparison](https://github.com/bitcoinknots/bitcoin/compare/v29.4.1.knots20260508...v29.4.2.knots20260508)
were reviewed against these implementation boundaries:

| Source at `58398baf...` | Contract and Miningcore consequence |
| --- | --- |
| `src/kernel/chainparams.cpp`, `consensus/params.h` | Mainnet start/enforce 973440, release 979920 exclusive, maturity 6480. Regtest has explicitly unscheduled defaults; reviewed fixture overrides require three distinct, validated boundaries. |
| `src/consensus/tx_verify.cpp`, `validation.cpp`, `txmempool.cpp` | Consensus selectively covers coinbases during enforcement; mempool applies long maturity to every coinbase. Ordinary transaction inputs do not inherit a coinbase lock. |
| `src/rpc/blockchain.cpp`, `rpc/mining.cpp` | Deployment `active` and GBT rules describe the next block. RPC `height_end` is inclusive. `getdifficulty` is removed; header-v2 reports expected hash work via `difficulty_blake2b`. |
| `src/node/interfaces.cpp`, `wallet/wallet.cpp`, `wallet/receive.cpp` | Wallet maturity persists independently of deployment inactivity. Remaining depth is `max(0, maturity + 1 - confirmations)`. |
| `src/node/blockstorage.cpp`, `validation.cpp`, `init.cpp` | Upgrade can rewind/revalidate cached chainstate. Pruned nodes may need resynchronization. Mining and payouts must wait for valid chain state and wallet indexing. |
| `primitives/block.h`, `primitives/block.cpp`, `pow.cpp`, `test/data/block_header_v2.json` | No changes to header-v2 primitives, PoW or official vectors in this release's net diff. Existing vector provenance and accounting identities are retained. |

The released schedule is modeled separately from persistent wallet policy and operator
confirmations. Every successful startup/runtime maturity attestation must match the
reviewed RPC field set, values and next-height semantics. An unexpected MTP field or
scheduled deployment on default regtest is rejected. GBT checks run on every template,
including inside the identity cache, so enforcement/release transitions do not depend
on cache expiry. Transport failures withhold fresh work; successful contradictions use
the existing pool-local fail-stop and accounting admission boundary.

Reward classification independently attests identity/chain/deployment each pass and
requires a synchronized chain. It verifies the generated transaction identity and the
matching active block. Progress uses the smaller wallet/active-chain confirmation
count, preventing an intervening downward reorg from unlocking on stale wallet depth.
Missing wallet records keep active or unverified blocks pending; only proven inactive
blocks become orphaned. Payout submission re-attests the contract and retains the
existing durable unknown-outcome/idempotent payment handling. Unsupported BLAKE2b
direct-coinbase settlement markers are refused, rather than being processed by
canonical Bitcoin's ordinary maturity path.

## Release verification

Use the upstream [release manifest and signatures](https://bitcoinknots.org/files/29.x/29.4.2.knots20260508/).
The reviewed archive SHA-256 values are:

| Archive | SHA-256 |
| --- | --- |
| `bitcoin-29.4.2.knots20260508-x86_64-linux-gnu.tar.gz` | `b59d0445a317e21a03dc29425db3aba79b27d5125230b1a2b1dce62e120827c5` |
| `bitcoin-29.4.2.knots20260508-win64-pgpverifiable.zip` | `8fa3445a0f3ecc7d1f9e4f4778e44c786883437ac781902a38135be5ea0a892b` |
| `bitcoin-29.4.2.knots20260508.tar.gz` (released source) | `11c0b99a82b8b1c9c29ab76d9b0507ce1017813665741627b3f3883a4c2f7a7f` |

The review verified `SHA256SUMS.asc` against `SHA256SUMS` with valid signatures from
Chris Guida (`658E64021E5793C6C4E15E45C2E581F5B998F30E`) and bitcoinmechanic
(`95636F3538D9262765AB29BEE952E584CA8C0F45`). Public keys came from the official
[Knots builder-key repository](https://github.com/bitcoinknots/guix.sigs/tree/b5a8daaab328ba1d6e2d4af1613d4b42e0878481/builder-keys)
in an isolated keyring. This establishes mathematical signature validity against
those published keys; operators must independently establish signer trust.

Before running a production binary, acquire the release archive, manifest and detached
signature, establish trusted signer fingerprints, then verify:

```bash
gpg --status-fd 1 --verify SHA256SUMS.asc SHA256SUMS
# Require VALIDSIG for an independently trusted reviewed signer; inspect any
# unknown or invalid signatures rather than interpreting partial output as success.
sha256sum --ignore-missing --check --strict SHA256SUMS
```

Check that the exact selected archive was present and passed; checking other files
does not verify a missing archive. Startup RPC strings are compatibility checks and
do not authenticate a downloaded binary. CI pins the Linux binary checksum and the
signed-release source checksum; it does not import arbitrary network signing keys.

## Stop, upgrade, reconcile, restart

1. Close new mining/PPS/payment admission and perform the documented graceful drain.
   Keep accepted shares, the accounting/recovery journals, uncertain candidates,
   payment-batch identities and database backups. Do not cancel owned submissions
   or erase unresolved payment outcomes to force an upgrade.
2. Stop Miningcore and the daemon fully. Back up the dedicated wallet, node and ledger.
   Verify the selected binary/release first. Never replace the running endpoint's
   binary or chain settings underneath an active pool.
3. Start the reviewed daemon with its existing chain data and dedicated wallet.
   Allow upgrade revalidation/resynchronization to finish, including any required
   pruned-node resync. Inspect chain, synchronization, deployments and GBT. Allow
   wallet indexing to catch up. Do not reduce confirmations to hide pending rewards.
4. Deploy this Miningcore compatibility update and preserve the existing coin key,
   pool IDs and ledger symbol. Restart and reconcile pending/uncertain candidates
   and payment batches through the existing durable paths. A confirmed or orphaned
   block does not reprice or reverse already-booked PPS liabilities.
5. Compare immature wallet funds, mature spendable liquidity and outstanding balances
   with the [PPS reserve policy](pps.md). The long wallet lock survives consensus
   release. Resume PPS admission only when independently monitored mature reserves
   cover the operator's policy; Miningcore has no automatic solvency guarantee.
6. Commission the supported miner/Stratum profiles against the exact target boundary
   and verify fresh work, accepted block identities and reward states before exposing
   the pool. Unknown builds/rules require a new source/build review and regression
   evidence, including authenticity and accounting-admission behavior.

## Reproducible tests

Set `MININGCORE_TEST_BLAKE2B_BITCOIND` to the verified binary and
`MININGCORE_TEST_POSTGRES` to an isolated test database. Run:

```bash
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --filter FullyQualifiedName~BitcoinBlake2b
bash scripts/regtest/validate-knots-29.4.2-maturity.sh
```

The second command downloads checksum-pinned released source and runs its unmodified
`mempool_long_coinbase_maturity.py` and `feature_chainstate_revalidation.py`. It verifies both scripts against the reviewed
commit's file hash and owns temporary data directories only. Its distinct schedule
`2:104:106` tests before/at coverage, before/at enforcement, before/at release,
all-coinbase mempool policy and invalid regtest switches. The revalidation test
restarts inherited ordinary-maturity chainstate with long enforcement, verifies
selection of a valid side branch and eviction of premature coinbase spends while
ordinary transactions remain in the mempool, then checks persisted validation
markers and demotion of stale side branches. CI runs both gates.

Miningcore's `30:134:140` fixture additionally checks header-v2 GBT rule transitions,
pre-coverage wallet rewards, ordinary non-coinbase spending, confirmation 110 versus
111, restart/reorg/reconsideration, contract outages/drift and a downward reorg between
wallet and chain reads. The `2:201:202` fixture verifies insufficient mature wallet
funds do not create payment records. Production-container startup verifies removed
`getdifficulty`, old SHA256d header fields, new next-block expected work and unchanged
target-derived job difficulty. Existing tests exercise all four ASIC layouts, real
Stratum proofs/accepted blocks and PostgreSQL exactly-once credit, payout schemes,
conflicting replay rejection and preserved PPS liabilities after orphaning.

Live validation on 2026-10-04 used the documented Windows/Ubuntu 22.04 WSL lab,
official Knots binaries and isolated PostgreSQL. The full Linux suite passed
**3570 tests, zero failures, one skipped benchmark**, including real Knots, Bitcoin
Core 28.1, Litecoin 0.21.5.5, Dogecoin 1.14.9, PostgreSQL accounting and isolated
authentication/TLS/recovery fault tests. Use CI's native-library `LD_LIBRARY_PATH`
and `MININGCORE_TEST_POSTGRES_BIN` settings to include those native/fault checks.
Both unmodified upstream tests passed through the reproducible CI script.
Windows compatibility/ledger tests passed **422 tests, zero failures/skips**;
the final maturity-focused run passed **45 tests, zero failures/skips**. Windows
used the documented local `MININGCORE_WINDOWS_PLATFORM_TOOLSET=v142` compatibility
setting because v143 was unavailable. A broader Windows run exposed a TLS fingerprint
assertion failure reproduced on unchanged `dev`; its Linux counterpart passed.
Source/test presence alone is not validation evidence.

## DATUM handoff to #163

[DATUM server support remains #163](https://github.com/NINJAK1DD/miningcore/issues/163).
This PR establishes its reviewed node/RPC/maturity prerequisite and preserves
ordinary miner-facing Stratum. It does not implement or certify a DATUM server.

| Component | Reviewed reference | Support decision |
| --- | --- | --- |
| Knots node | `58398baf33e588779685ead478e6397bb28ed3d6`, released 29.4.2 | Accepted by this compatibility gate |
| CONVOY gateway | `ac9b70c8b361f14e90e2c963b429a9bbb414aecb` | Interoperability design/test reference; no Miningcore DATUM certification |
| RATUM | `b3d152d7a5572d95bfbe8a44ce87524ed095b3a8` | RPC/protocol reference; no implementation code incorporated |

The [CONVOY comparison](https://github.com/CONVOYMining/datum_gateway/compare/b9ea7dc3eb91352565ab487ec55ed6ee5964a440...ac9b70c8b361f14e90e2c963b429a9bbb414aecb)
was reviewed for subtraction-based overflow-safe output tally checks, header-v2 large
coinbase selection and ABW assignment/proof lifecycle. Future #163 tests must cover
initial/rotated assignment refresh, receipts/reveals, submission despite disclosure-cache
exhaustion, local revealed-block submission before audit-failure handling and reconnects.
Gateway minimum difficulty is configuration, not consensus or accounting rescaling.
Preserve current block/coinbase budgets despite gateway support for larger coinbases.
Do not claim an audited anti-withholding guarantee for gateways that cannot retain/audit
proofs. Pin supported gateway/protocol versions before implementing the server.

The reward state contract for #163 must separate allocation, inclusion, immature
receipt, verified spendability, orphaning and deferred wallet settlement. Store direct
allocation/settlement identities durably; the wallet payout scheduler must never pay
direct recipients a second time. Coinbase inclusion alone cannot settle a spendable
reward. Reorg/restart tests must preserve deferred entitlements and existing PPS
liabilities without double credit/payment. End-to-end gateway/node/miner and mature
versus immature direct-reward acceptance remains open until #163 is implemented.

CONVOY's reviewed license is MIT-style; RATUM's is AGPL-3.0. Only source research and
requirements were used here. Review licensing before incorporating code in #163.

## Merge-time review gate

As of the 2026-10-04 recheck, Knots 29.4.2 remained the latest published release and
the reviewed default head. [#429](https://github.com/bitcoinknots/bitcoin/pull/429)
and [#434](https://github.com/bitcoinknots/bitcoin/pull/434) were open maturity proposals;
their MTP/phased rules are not installed by this change. Recheck both and release/head
status **immediately before merge**. If additional rules have shipped, review the exact
source, activation and RPC contract as additional scope before accepting another build.
No proposed reward eligibility rule, address blacklist or draft P2P settlement is
implemented. Released-source review is not a measurement of network-wide adoption.
