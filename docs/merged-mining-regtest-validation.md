# Litecoin–Dogecoin merged-mining regtest validation

This file records live validation performed against PR #18. It complements the automated tests; it
is not a promise that an arbitrary mainnet deployment is production-safe.

## Environment

- Date: 12 July 2026
- Miningcore: `feature/ltc-doge-merged-mining-clean`
- Host: Miningcore on Ubuntu 22.04 under WSL2; CUDA 10 ccminer 2.3.1 on Windows
- Litecoin Core: 0.21.5.5 (`/LitecoinCore:0.21.5.5/`), regtest, one peer
- Dogecoin Core: 1.14.9 (`/Shibetoshi:1.14.9/`), regtest, one peer
- PostgreSQL: 17.10
- RPC fault injection: `scripts/regtest/rpc_fault_proxy.py`

The tests used distinct LTC and DOGE payout addresses and a real Scrypt GPU miner. Database checks
were made against the same PostgreSQL instance used by the running payout manager.

## Results

| Production gate | Status | Evidence or remaining work |
| --- | --- | --- |
| Real `litecoind` and `dogecoind` | Passed | Both pools started, served combined jobs and accepted mined blocks. |
| Real PostgreSQL | Passed | Real inserts, partial unique indexes, rollbacks and concurrent transactions were exercised. |
| Dual-target proof | Passed | One proof produced independent payable LTC `merged-parent` and DOGE `auxpow` rows. |
| Litecoin-only proof | Passed | The fault proxy advertised a harder DOGE target while leaving Litecoin at regtest minimum. Miningcore submitted only Litecoin, which accepted block 291; exactly one new `merged-parent` row was recorded. |
| Dogecoin-only proof | Passed | The fault proxy advertised a harder Litecoin target while leaving DOGE at regtest minimum. Miningcore submitted only AuxPoW, which Dogecoin accepted at height 212; exactly one new `auxpow` row was recorded. |
| Competing parent proofs | Passed | Six different valid parent proofs were submitted for one frozen DOGE child. Dogecoin returned `true` for all; only the header matching `getblock.auxpow.parentblock` was credited. |
| Lost DOGE HTTP response | Passed | The proxy forwarded `submitauxblock`, dropped the response, and Miningcore recovered the matching active proof without duplicate rows. |
| Boolean-positive duplicate DOGE submission | Passed | Competing valid proofs returned Boolean success; mismatching parent headers were rejected for accounting. |
| Litecoin JSON-null response | Passed | A forwarded accepted block had its response replaced with JSON null and was recovered by exact active-hash lookup. |
| Litecoin `inconclusive` response | Passed | The replacement fault fired once; the active block and coinbase were recovered into one idempotent parent row. |
| Duplicate parent/recovery replay | Passed | The same real `merged-parent` block-only recovery record was imported twice; both imports exited and one database row remained. |
| Atomic ordinary-share recovery | Passed | Recovery holds the source read-locked, validates every record before opening a transaction, then imports all batches in one transaction. Tests cover a malformed record after 100 valid shares, valid/invalid/valid input, second-batch database rollback followed by safe full retry, exact valid-file batching and block-only replay. A real Linux recovery run with 100 valid records followed by malformed input exited 1 while PostgreSQL remained at 24 shares before and after. |
| Lower-height Litecoin reorganisation | Passed | An authoritative lower template with a changed `previousblockhash` replaced the job and retained the cached DOGE template. |
| Dogecoin reorganisation | Passed | An accepted child was invalidated, returned `confirmations = -1`, and its row became orphaned; the chain was then restored. |
| Litecoin MWEB template | Passed | The original failure skipped Litecoin Core's required pre-activation pegin. Repeating the [official v0.21.5.5 functional-test sequence](https://github.com/litecoin-project/litecoin/blob/v0.21.5.5/test/functional/mweb_mining.py) produced a height-432 template with a 4,198-character `mweb` payload. CUDA ccminer then submitted through Miningcore; Litecoin accepted blocks 433 and 434, and verbose `getblock` returned valid MWEB extension data for both. |
| Parent wallet-index lag | Passed | Repeated Litecoin `gettransaction -5` responses retained exact active parent rows as pending. |
| Dogecoin wallet-index lag | Passed | Repeated DOGE `gettransaction -5` responses retained exact active AuxPoW rows; pass-through recovery resumed normal maturity checks. |
| Coinbase maturity and wallet credit | Passed | Earlier blocks matured and produced actual 100 LTC and 1,000,000 DOGE payment records in this regtest environment. |
| Claim promotion vs direct insert | Passed | Twenty real PostgreSQL races retained exactly one payable AuxPoW row, including uniqueness-conflict rollback/retry outcomes. |
| Two concurrent claim promotions | Passed | One guarded update affected one row, the other affected zero; both transactions committed with `1 auxpow : 0 claims`. |
| PostgreSQL replay/idempotency | Passed | Final AuxPoW, proof claims and accepted/uncertain merged-parent replay each retained one protected identity. |
| Reordered JSON-RPC batch | Passed | Both pools initialized and Stratum authorized while the proxy reversed batch response arrays, validating ID correlation. |
| Relay disconnect/reconnect | Passed | Separate Linux sender and receiver processes exercised real Stratum, ZeroMQ and PostgreSQL. After the publisher was absent beyond the production 60-second timeout, the unchanged receiver reconnected and persisted new ordinary shares plus LTC/DOGE block-only rows. Receiver restart also reacquired ownership and resumed persistence. A different physical-host/firewall path should still be checked for the final deployment. |
| Accidental dual payout manager | Partial guard passed / issue #19 open | A PostgreSQL session advisory lock rejected a second healthy receiver and a clean restart released then reacquired the lock. The lock does not fence work already in progress if its backend is terminated. Automatic/hot-standby payout failover remains unsupported pending durable block/payment fencing and active-cycle interruption tests. |
| Asynchronous block delivery decision | Explicit opt-in | The current recorder handoff and ZeroMQ PUB/SUB model is retained, but it is no longer silently accepted: every enabled merged-mining pool must set `acceptNonDurableBlockDelivery=true`. Startup otherwise fails with the documented process-crash and disconnected-receiver loss windows. |

## Runtime hardening observed during validation

- A normal systemd stop now completes in about 0.8 seconds without SIGKILL or a libzmq assertion.
- Recovery import is a one-shot mode: it does not start normal payout/statistics/relay background
  services and stops the host after success or failure. Missing, malformed or database-failed
  imports return exit code 1 only after ensuring that the complete file wrote nothing; rerunning the
  original failed input is therefore safe.
- Fault rules can require a minimum parameter count, preventing a block-submission fault from being
  consumed by Miningcore's parameterless startup capability probe.
- Fault rules can patch nested template results, allowing deterministic target-separated parent-only
  and auxiliary-only daemon-backed tests without changing chain consensus.
- Every applied proxy response mutation is written to the JSONL fault log.
- Relay receiver pools without internal Stratum remain alive until host cancellation, and recovery
  imports configure neither ordinary background services nor the API web host.
- A database-scoped PostgreSQL advisory lock prevents accidental concurrent healthy payout-manager
  startup and is checked before each cycle. It does not fence financial work already in progress.

## Remaining mainnet gates

Do not enable mainnet funds solely because the passed rows above are green. Repeat relay recovery
across the exact physical hosts, firewall and network path planned for deployment. The documented
asynchronous recorder and ZeroMQ PUB/SUB loss windows remain an explicitly accepted operational risk
when `acceptNonDurableBlockDelivery=true`; deployments that cannot accept that risk still require a
transactional outbox, synchronous repository handoff or acknowledged transport.
Automatic/hot-standby payout-manager failover is unsupported. Confirm the old process has fully
terminated before starting a replacement until issue #19 adds durable fencing, payout-batch
idempotency and active-cycle interruption tests.
