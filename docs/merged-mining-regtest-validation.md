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
| Losing-claim balance side effects | Passed | A real PostgreSQL direct-final-vs-claim race produced `0` losing balances, `0` losing balance-change rows, `1` winner balance, `1` winner balance-change row and `1` payable finalized AuxPoW row. The repeatable harness is `scripts/regtest/postgres-claim-balance-race.sh`. |
| Two concurrent claim promotions | Passed | One guarded update affected one row, the other affected zero; both transactions committed with `1 auxpow : 0 claims`. |
| PostgreSQL replay/idempotency | Passed | Final AuxPoW, proof claims and accepted/uncertain merged-parent replay each retained one protected identity. |
| Reordered JSON-RPC batch | Passed | Both pools initialized and Stratum authorized while the proxy reversed batch response arrays, validating ID correlation. |
| Mixed-version accepted-parent relay safety | Automated | Once the synchronous `merged-parent` copy commits, the original proof is serialized with `IsBlockCandidate = false`. A base-compatible protobuf receiver model ignores the new field and still cannot attempt another block insert. |
| PostgreSQL custom-schema preflight | Automated real PostgreSQL | CI and the local PostgreSQL integration test select valid and stale same-named `blocks` relations through different `search_path` orders. Exact canonical keys and predicates are required; `lower(poolid)`, reversed keys, `lower(hash)` and a narrower status predicate are rejected. |
| Delayed parent winning share and effort | Automated real PostgreSQL | Direct and relay merged-parent processing waits one minute, the relayed parent preserves its originating timestamp, and an ordinary share inserted later at the exact block timestamp is included by the final effort query. |
| Independent submission failure drain | Automated | Parent failure/DOGE success, DOGE failure/parent success, one timeout plus peer acceptance, and dual failures all drain both bounded tasks before one or all errors propagate. |
| Statistical share independent of submissions | Automated | A cleared ordinary share is published immediately after proof validation, before parent or DOGE submission starts. Peer timeout/persistence failure cannot suppress it, and current receivers preserve a parent candidate's originating effort-boundary timestamp. |
| Alephium sweep transaction identities | Automated | Null/empty sweep envelopes, null entries and blank transaction IDs are financially uncertain; valid multi-result sweeps require every identity. |
| Relay disconnect/reconnect | Passed | Separate Linux sender and receiver processes exercised real Stratum, ZeroMQ and PostgreSQL. After the publisher was absent beyond the production 60-second timeout, the unchanged receiver reconnected and persisted new ordinary shares. LTC/DOGE block-only rows are now synchronously persisted by the sender and do not depend on relay availability. The final physical path can be repeated with `scripts/regtest/validate-physical-relay.sh`. |
| Accidental dual payout manager | Passed | Real PostgreSQL advisory-backend termination stopped generation 1 but left its durable owner token. A normal replacement process was rejected until the dead PID was confirmed and the marker explicitly cleared. Controlled recovery acquired generation 2; clean stop cleared it and generation 3 started normally. Concurrent pending-block transactions produced one 25-unit balance credit and one terminal row, while concurrent payment persistence produced one batch and one balance reset. |
| Wallet accepted, response lost during shutdown | Passed | The Dogecoin fault proxy forwarded `sendmany`, captured accepted txid `4646670c...eaa4`, delayed the response and dropped it while systemd stopped Miningcore in 0.85 seconds. The database balance/payment rows remained unchanged, generation 6 ownership remained durable, and replacement startup was rejected. After `gettransaction` reconciliation, the exact txid was persisted once, the test balance was reset, the dead owner was explicitly released and generation 7 started normally. |
| Successful recovery-file replay | Passed plus automated hardening | A valid ordinary-share file imported once and was renamed with `.imported-<timestamp>`. The automated regression rejects the same normalized record multiset even when records are reordered or comments, JSON whitespace and line endings differ. Four domain-separated modular accumulators plus cardinality retain only 128 bytes of digest state; a one-million-record regression verifies the state does not grow with the journal. |
| Durable merged-block delivery | Automated shutdown regression passed; live failure injection still required | Accepted and uncertain LTC/DOGE block-only candidates are synchronously committed by the submitting node before submission returns. A real Generic Host test holds a candidate beyond .NET 6's former five-second default and verifies shutdown waits for a bounded PostgreSQL attempt and forced recovery-journal flush. During quiescence the recorder skips the ordinary retry delays; failure of both PostgreSQL and the journal still propagates. Atomic recovery import/replay has been tested, but no recorded live run has yet accepted an LTC/DOGE block while PostgreSQL was unavailable under this final synchronous architecture. |

## Runtime hardening observed during validation

- A normal systemd stop now completes in about 0.8 seconds without SIGKILL or a libzmq assertion.
- Recovery import is a one-shot mode: it does not start normal payout/statistics/relay background
  services and stops the host after success or failure. Missing, malformed or database-failed
  imports return exit code 1 only after ensuring that the complete file wrote nothing; rerunning the
  original failed input is therefore safe. Successful input is registered by SHA-256 in PostgreSQL
  and archived with an `.imported-<timestamp>` suffix, preventing ordinary-share replay.
- Fault rules can require a minimum parameter count, preventing a block-submission fault from being
  consumed by Miningcore's parameterless startup capability probe.
- Fault rules can patch nested template results, allowing deterministic target-separated parent-only
  and auxiliary-only daemon-backed tests without changing chain consensus.
- Every applied proxy response mutation is written to the JSONL fault log.
- Litecoin and Dogecoin fault proxies are installed as enabled systemd template instances and are
  required dependencies of `miningcore-regtest.service`. A controlled WSL stop/start through the
  Windows keepalive launcher restored both proxies, both pools, PostgreSQL connectivity and Stratum
  automatically; payout ownership advanced to a fresh generation without manual proxy startup.
- Relay receiver pools without internal Stratum remain alive until host cancellation, and recovery
  imports configure neither ordinary background services nor the API web host.
- A database-scoped advisory lock prevents concurrent healthy payout-manager startup. A durable
  ownership row survives lock-session/process loss, so replacement startup remains fail-closed.
  Pending block rows are locked and must win their guarded terminal transition before any reward
  balance mutation; committed wallet transaction IDs are
  idempotent payment-batch keys so database retry cannot reset balances twice.

## Remaining mainnet gates

Do not enable mainnet funds solely because the passed rows above are green.

1. **Physical relay route — conditional, not yet recorded for final hosts.** The separate sender and
   receiver process test passed on one Linux host. If production uses relay, repeat ordinary-share
   recovery across the exact production hosts, firewall and route with
   `bash scripts/regtest/validate-physical-relay.sh` on the final receiver. If production is a direct
   single-node deployment, this gate is not applicable.
2. **Accepted block during PostgreSQL interruption — still outstanding.** Recovery-file import,
   replay protection and database failure behavior passed separately, but the validation record does
   not contain a live LTC/DOGE accepted block whose synchronous PostgreSQL write failed and whose exact
   block-only record was flushed to and recovered from the write-through journal.
3. **Payout-manager ownership — tests passed; operating rule remains.** The fail-closed backend
   termination, controlled recovery, block-credit serialization and payment-batch idempotency tests
   passed against PostgreSQL 17. Automatic/hot-standby failover remains intentionally unsupported.
   After every unclean stop, confirm the old process has fully terminated and reconcile wallet history
   before explicitly clearing its durable ownership row.
