# Custodial coinbase confirmation policy

Issue [#259](https://github.com/NINJAK1DD/miningcore/issues/259) corrects the shared
Bitcoin-family custodial classification boundary. Wallet maturity alone previously
allowed a reward to bypass an operator's larger `minimumConfirmations` value.

## Research and decision

Reviewed on 7 November 2026 against Miningcore `dev` at `8456642` and:

- [Bitcoin Core gettransaction contract](https://bitcoincore.org/en/doc/30.0.0/rpc/wallet/gettransaction/).
- [Litecoin Core v0.21.5.8 wallet RPC source](https://github.com/litecoin-project/litecoin/blob/v0.21.5.8/src/wallet/rpcwallet.cpp).
- [Dogecoin Core v1.14.9 wallet RPC source](https://github.com/dogecoin/dogecoin/blob/v1.14.9/src/wallet/rpcwallet.cpp).

The wallet category derives from network coinbase maturity. It is independent of
Miningcore's confirmation policy. `generate` is necessary but does not establish
the operator's required depth. A lower override cannot bypass an `immature` category.

The existing threshold precedence is retained: pool `minimumConfirmations`, then
coin-template `coinbaseMinConfimations` (existing spelling), then family default 102.
The shared classification path requires positive integer JSON confirmation evidence;
numeric strings, fractional values, nulls, missing values and overflow cannot unlock.
Nonpositive thresholds fail closed even when configuration validation is bypassed.

The wallet reward must identify the persisted block and coinbase, have coinbase
index zero, a nonnegative numeric amount, the generated flag and consistent categories,
without wallet conflicts. Before settlement, `getblock` independently verifies the
same active hash, height, sufficient depth and first transaction. The two observations
need not report identical depths because a new tip can arrive between RPC calls;
both must satisfy the policy. This does not make observation and database commit
atomic with blockchain consensus, nor eliminate future reorganisation risk.

Invalid observations preserve existing reward and coinbase evidence for retry.
Valid pending observations refresh the reward and progress. Progress is the depth
divided by the effective threshold, capped at 99.9% while wallet maturity or chain
verification still prevents settlement. Missing wallet evidence and unknown categories
cannot prove an orphan. An orphan category or wallet miss requires independent
inactive-block evidence; active or unavailable observations stay pending.

## Financial regression coverage

- `BitcoinPayoutHandlerTests.CustodialConfirmation_*` exercises the actual handler
  at LTC 101/102/103 and DOGE 61/250/251/252; lower/higher overrides, template/family
  defaults, malformed evidence, repeated observations and recovered chain evidence.
- `BitcoinCustodialSettlementIntegrationTests` uses isolated PostgreSQL, production
  repositories, the real handler, SOLO scheme and both payout-manager stages. Controlled
  wallet RPC responses cover BTC, LTC `merged-parent` and DOGE `auxpow` independently.
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

Custodial confirmed rows retain the existing terminal accounting contract: a later
reorg does not automatically debit miners or replay the reward. The PostgreSQL test
checks this no-duplicate boundary; it does not claim recovery of already-spent funds.
No production wallet, candidate record or payout reconciliation is modified.

## Running

Validated on 7 November 2026 in the documented Ubuntu 26.04 WSL lab with .NET
10.0.114, Bitcoin Core 31.1.0, Litecoin Core 0.21.5.8, Dogecoin Core 1.14.9 and
PostgreSQL 18.6. The full native/managed build and subsequent managed rebuild both
reported zero warnings and errors. All **51 new cases passed**, including the three
PostgreSQL settlement cases and real-daemon boundaries. All 13 cases in the selected
direct-coinbase test class passed.

The relevant suite finished with **538 passing tests and one optional skip**, after
rerunning one existing PPS migration test with the administrator privileges its
fixture requires. The first run had 537 passes, one role-policy failure and one skip;
the failed test then passed in a disposable loopback-only PostgreSQL cluster. No
persistent lab role was elevated. The skipped test requires the separate Bitcoin
BLAKE2b Knots daemon, outside this shared-handler change. Logs and TRX results are
retained in the isolated lab checkout under `build/custodial`; there are no unresolved
test failures in the selected scope.

Use the documented persistent test lab with isolated daemon datadirs and PostgreSQL
schemas. Set `MININGCORE_TEST_BITCOIND`, `MININGCORE_TEST_LITECOIND`,
`MININGCORE_TEST_DOGECOIND`, `MININGCORE_TEST_POSTGRES` and
`MININGCORE_TEST_POSTGRES_BIN`. Build the native dependencies before running:

```sh
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build \
  --filter 'FullyQualifiedName~BitcoinPayoutHandler|FullyQualifiedName~BitcoinCustodial|FullyQualifiedName~MergedMining|FullyQualifiedName~PayoutManager|FullyQualifiedName~PayoutHandlerPersistence|FullyQualifiedName~DirectCoinbase'
```
