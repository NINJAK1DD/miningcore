# Custodial coinbase confirmation policy

Issue [#259](https://github.com/NINJAK1DD/miningcore/issues/259) corrects the shared
Bitcoin-family custodial classification boundary. Wallet maturity alone previously
allowed a reward to bypass an operator's larger `minimumConfirmations` value.

## Research and decision

Reviewed on 7 November 2026 against Miningcore `dev` at `8456642` and:

- [Bitcoin Core gettransaction contract](https://bitcoincore.org/en/doc/30.0.0/rpc/wallet/gettransaction/).
- [Litecoin Core v0.21.5.8 wallet RPC source](https://github.com/litecoin-project/litecoin/blob/v0.21.5.8/src/wallet/rpcwallet.cpp).
- [Dogecoin Core v1.14.9 wallet RPC source](https://github.com/dogecoin/dogecoin/blob/v1.14.9/src/wallet/rpcwallet.cpp).
- [Nexa block RPC source](https://gitlab.com/nexa/nexa/-/blob/dev/src/rpc/blockchain.cpp),
  `blockheaderToJSON` and `blockToJSON`: the non-verbose transaction-ID list is
  `txid`; `txidem` is a separate identity and must not be substituted for it.

The wallet category derives from network coinbase maturity. It is independent of
Miningcore's confirmation policy. `generate` is necessary but does not establish
the operator's required depth. A lower override cannot bypass an `immature` category.

The existing threshold precedence is retained: pool `minimumConfirmations`, then
coin-template `coinbaseMinConfimations` (existing spelling), then family default 102.
The shared classification path requires positive integer JSON confirmation evidence;
numeric strings, fractional values, nulls, missing values and overflow cannot unlock.
Nonpositive effective thresholds throw `PoolStartupException` in `ConfigureAsync`,
including invalid template defaults. They cannot silently leave every reward pending.
The settlement guard also retains its defensive positive-threshold check.

The wallet reward must identify the persisted block and coinbase, have coinbase
index zero, a nonnegative numeric amount, the generated flag and consistent categories,
without wallet conflicts. Before settlement, `getblock` independently verifies the
same active hash, height, sufficient depth and first transaction. The two observations
need not report identical depths because a new tip can arrive between RPC calls;
both must satisfy the policy. This does not make observation and database commit
atomic with blockchain consensus, nor eliminate future reorganisation risk.

The shared handler also serves Nexa. Its block lookup decodes the family-specific
`txid` list and maps hash, height, depth and transactions into the verification model.
Bitcoin-family lookups retain their `tx` contract; neither family accepts the other's
field as a fallback. RPC errors survive normalization and cannot authorize settlement.

Invalid observations preserve existing reward, coinbase evidence and trustworthy
progress for retry. A pending row's progress is normalized to the range 0–99.9%;
non-finite legacy values become zero. Valid pending observations refresh the reward
and progress, and publish progress notifications only when that progress changes.
Progress is the depth
divided by the effective threshold, capped at 99.9% while wallet maturity or chain
verification still prevents settlement. Missing wallet evidence and unknown categories
cannot prove an orphan. An orphan category or wallet miss requires independent
inactive-block evidence; active or unavailable observations stay pending.

## Delayed evidence and operator alerts

All custodial stalls use a separate `{Type}:custodial-evidence` episode keyed by pool
and persisted row ID, including legacy rows whose hash is populated later. Invalid
wallet JSON/fields, unsupported categories, wallet/chain disagreement, missing wallet
transactions/details, wallet RPC errors and failed mature chain verification emit
one warning per episode. The warning contains a fixed reason code, not raw daemon
messages or response payloads. After 30 minutes of continuously unverifiable evidence,
one `FinancialOrSecurity` admin notification is sent. Failed publication can retry.
Actual valid immature progress or successful settlement clears the episode; merely
finding an active chain block while the wallet reward is still missing does not.
The tracker survives handler recreation within a process, but is in memory: a process
restart starts a new grace period. Notification delivery follows the configured
[administrative notification channels](notifications.md).

| Fixed reason group | Operator checks |
| --- | --- |
| `wallet-response-malformed`, `wallet-confirmations-invalid`, `wallet-generated-invalid`, `wallet-coinbase-index-invalid`, `wallet-amount-invalid`, `wallet-categories-inconsistent`, `wallet-category-unsupported` | Deployed daemon contract and proxy response transformations |
| `wallet-conflicts`, `wallet-transaction-identity-mismatch`, `wallet-block-identity-mismatch` | Wallet/chain agreement and persisted coinbase identity |
| `active-block-wallet-transaction-missing`, `active-block-wallet-details-missing` | Wallet ownership, import/rescan and indexing; an active block alone does not prove the pool can spend its reward |
| `wallet-rpc-error`, `wallet-missing-chain-unavailable`, `wallet-details-missing-chain-unavailable`, `wallet-orphan-chain-unverified` | Node synchronization, wallet availability and permitted RPC methods |
| `chain-activity-unverified`, `chain-depth-below-policy`, `chain-height-mismatch`, `chain-coinbase-mismatch` | `getblock` access, active chain, depth, height and first transaction |

The subject is now `[pool] block reconciliation delayed`, replacing the older
`[pool] merged-mining block reconciliation delayed`; update subject-based filters.

**Unknown blocks are no longer automatically orphaned.** A `getblock` -5/-1 after a
resync, node replacement or pruning does not establish that a previously observed
coinbase is inactive. Such ordinary custodial rows remain pending, retain their
evidence, alert after the grace period and retry RPC on subsequent payout cycles
without a time-based expiry. This deliberately differs from the bounded reconciliation
of uncertain submission markers: discarding a persisted reward solely because a
replacement node cannot find it could erase a genuine liability. Independently
verified inactive blocks still become orphaned. Restore a suitable synchronized node
and owning wallet; do not rewrite balances or mark rows paid to silence an alert.

## Shared-handler compatibility and remaining trust boundary

The [source inventory and recovered upstream links](custodial-source-compatibility.md)
cover all 180 routed templates: 149 Bitcoin, 19 Progpow, ten Equihash, one Nexa and
one Satoshicash. Equihash inherits this classification even when its payout override
is disabled. Bitcoin BLAKE2b overrides classification and is outside this inventory.
Source-field discovery and representative HTTP tests do not certify every daemon
version or every current mainnet. Missing or incompatible evidence remains pending
and visible through the warning/grace-alert behavior above.

The custodial amount remains the owning wallet's numeric `amount`. The separate
`getblock` lookup binds identity and depth; it does not independently sum pool-owned
coinbase outputs. Both calls rely on a trusted daemon/proxy, not an independent
consensus oracle. General output validation needs chain-specific transaction formats,
historical payout addresses and reward-recipient/fee accounting, so it is recorded
as separate hardening rather than adding an unvalidated cross-family amount rule.
The live regressions independently decode outputs to validate observed rewards.
This change does not protect against a compromised RPC node inventing consistent
wallet and block responses, nor make RPC observations atomic with database commit.

## Financial regression coverage

- `BitcoinPayoutHandlerTests.CustodialConfirmation_*` exercises the actual handler
  at LTC 101/102/103 and DOGE 61/250/251/252; lower/higher overrides, template/family
  defaults, malformed evidence, repeated observations and recovered chain evidence.
- `BitcoinCustodialSettlementIntegrationTests` uses isolated PostgreSQL, production
  repositories, the real handler, SOLO scheme and both payout-manager stages. Controlled
  wallet RPC responses cover BTC, LTC `merged-parent`, DOGE `auxpow`, Nexa, Ravencoin,
  Satoshicash, Bitcoin Gold and Zcash independently.
  It checks zero premature balances, payments, batches and wallet submissions; forces
  credit failure after the block update; verifies rollback; restarts; and replays a stale
  observation under the row lock. Successful settlement creates one credit, one payment
  debit, one batch and one unlock notification.
- `MergedMiningPayoutRegtestTests.PinnedDaemons_WalletMaturityCannotBypassCustodialThreshold`
  mines isolated real LTC and DOGE wallet rewards, derives the reward independently
  from decoded coinbase outputs, checks the wallet-mature boundaries, invalidates and
  reconsiders the chain. This is a wallet/classification regression; the existing
  merged-mining proof and attribution suites cover actual AuxPoW submission.
- Existing Bitcoin direct-coinbase, wallet-index-lag, payout-manager and persistence
  tests remain part of the regression run.
- `BitcoinPayoutHandlerRegtestTests.CustodialCoinbase_WalletMaturityWaitsForConfiguredDepth`
  verifies Bitcoin Core's real `generate` reward at 101 stays pending until the
  family-default 102 confirmations, without submitting a wallet payment.
- `BitcoinPayoutHandlerTests.CustodialRpc_*` exercises the real HTTP RPC decoder and
  handler with Nexa-shaped `gettransaction`/`getblock` responses, including threshold
  boundaries, identity mismatches, inactive/short chains, absent/malformed transaction
  lists, RPC failures and cross-family field rejection. The PostgreSQL financial
  regression also includes Nexa. Representative Progpow, Satoshicash and both
  Equihash routing branches are exercised; inherited Equihash classification uses
  an actual `EquihashPayoutHandler`. Synthetic fixtures are identified as such.
- `Nexa_RealCoinbaseWalletAndChainContractsEnforceMaturityAndPolicy` mines isolated
  Nexa **2.2.0.0** rewards at depths 1/100/101/102/103, observes immature/mature
  categories, verifies `txid` differs from `txidem`, generated/index/hash fields,
  independently decodes output amounts, and checks low overrides plus
  invalidate/reconsider recovery. The committed
  [captured RPC fixture](../src/Miningcore.Tests/Fixtures/Rpc/nexa-2.2.0.0-regtest.json)
  is replayed through the real HTTP decoder without requiring a daemon in CI.
- `CustodialStall_*`, `CustodialProgress_*`, `CustodialLegacyRow_*` and
  `CustodialConfiguration_*` assert warning/alert suppression, grace/recovery,
  credential-safe diagnostics, stable progress, legacy episode cleanup and startup
  rejection. Ordinary inactive/unknown blocks and PostgreSQL progress persistence
  are covered alongside financial rollback/replay tests.

Custodial confirmed rows retain the existing terminal accounting contract: a later
reorg does not automatically debit miners or replay the reward. The PostgreSQL test
checks this no-duplicate boundary; it does not claim recovery of already-spent funds.
No production wallet, candidate record or payout reconciliation is modified.

## Running

Validated on 7 November 2026 in the documented Ubuntu 26.04 WSL lab with .NET
10.0.114, Bitcoin Core 31.1.0, Litecoin Core 0.21.5.8, Dogecoin Core 1.14.9,
Nexa 2.2.0.0 and PostgreSQL 18.6. The earlier full native/managed build and the final
managed rebuild reported zero warnings and errors. The expanded focused run passed
**1,706 tests, zero failures and 19 optional skips**. The subsequent full suite ran
**6,003 cases: 5,975 passed, 27 skipped and one diagnostic-operation inventory failure**.
That failure identified the renamed warning method missing from the reviewed
diagnostic allowlist. After correcting the production allowlist and rebuilding,
**all 100 diagnostics tests passed**, including the previously failing source audit.
All live BTC/LTC/DOGE/Nexa boundaries, eight PostgreSQL settlement cases, inherited
Equihash and captured Nexa decoder tests passed in the full run. Documentation
links and index validation passed for all 57 Markdown files.

The existing PPS migration fixture requires database administrator privileges.
The final run used a disposable loopback-only PostgreSQL cluster for that fixture;
no persistent lab role was elevated. Skips are optional/environment-specific cases,
including the separate Bitcoin BLAKE2b Knots daemon and other unconfigured live
chains, benchmarks and platform-specific checks. Logs and TRX results are
retained in the isolated lab checkout under `build/custodial`.

Use the documented persistent test lab with isolated daemon datadirs and PostgreSQL
schemas. Set `MININGCORE_TEST_BITCOIND`, `MININGCORE_TEST_LITECOIND`,
`MININGCORE_TEST_DOGECOIND`, `MININGCORE_TEST_NEXAD`, `MININGCORE_TEST_POSTGRES` and
`MININGCORE_TEST_POSTGRES_BIN`. Build the native dependencies before running:

```sh
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build \
  --filter 'FullyQualifiedName~BitcoinPayoutHandler|FullyQualifiedName~BitcoinCustodial|FullyQualifiedName~MergedMining|FullyQualifiedName~PayoutManager|FullyQualifiedName~PayoutHandlerPersistence|FullyQualifiedName~DirectCoinbase|FullyQualifiedName~Equihash'
```

Nexa's [official 2.2.0.0 release metadata](https://gitlab.com/api/v4/projects/nexa%2Fnexa/releases/nexa2.2.0.0)
links the [Linux x64 archive](https://www.bitcoinunlimited.info/nexa/2.2.0.0/nexa-2.2.0.0-linux64.tar.gz).
The checked SHA-256 is `83df20afc40f0e33c35b0212684f19bc699b655524c4d40fd03fb19d12b49de5`;
the daemon reports `v2.2.0.0-6651a9470`. Set `MININGCORE_TEST_NEXAD` to that binary.
The test creates its own loopback-only datadir with peer discovery disabled.
`MININGCORE_TEST_NEXA_CAPTURE` optionally writes a new public regtest observation
fixture (no wallet keys or RPC credentials); it is not required to run the test.
