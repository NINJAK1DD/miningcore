# Knots 29.4.2 compatibility review and upgrade

Reviewed on 2026-10-04 for [#205](https://github.com/NINJAK1DD/miningcore/issues/205),
following the exact-build policy in [#151](https://github.com/NINJAK1DD/miningcore/issues/151).
The accepted daemon is **29.4.2.knots20260508**, numeric version `290402`, with the
recognized `/Satoshi:29.4.2/Knots:20260508/` prefix. Printable `-uacomment` comments
in the Satoshi component and `-uaappend` suffixes are accepted within the reviewed
256-byte limit; malformed/spoofed or contradictory build prefixes are refused.
Protocol metadata is
`knots-29.4.2-header-v2`. Earlier 29.4.1 node guidance is historical provenance,
not an accepted current-mainnet build. There is no version bypass.

## Reviewed evidence

The [released source](https://github.com/bitcoinknots/bitcoin/tree/58398baf33e588779685ead478e6397bb28ed3d6)
and [29.4.1Ã¢â‚¬â€œ29.4.2 source comparison](https://github.com/bitcoinknots/bitcoin/compare/v29.4.1.knots20260508...v29.4.2.knots20260508)
were reviewed against these implementation boundaries:

| Source at `58398baf...` | Contract and Miningcore consequence |
| --- | --- |
| `src/kernel/chainparams.cpp`, `consensus/params.h`, `chainparams.cpp` | Mainnet start/enforce 973440, release 979920 exclusive, maturity 6480. Regtest has unscheduled defaults. Explicit fixture overrides use start >= 0, enforce >= 2, release > both, release-start > 100, all below INT_MAX. Enforce may precede start. |
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

The dedicated BLAKE2b payout handler independently attests identity/chain/deployment each pass and
requires a synchronized chain. It verifies the generated transaction identity and the
matching active block **header**, using `getblockheader` so block-body pruning cannot
strand mature rewards. Shared Bitcoin/AuxPoW paths retain their full-block lookups.
Progress uses the smaller wallet/active-chain confirmation
count, preventing an intervening downward reorg from unlocking on stale wallet depth.
Missing wallet records keep active or unverified blocks pending; only proven inactive
blocks become orphaned. Payout submission re-attests the contract and retains the
existing durable unknown-outcome/idempotent payment handling. Unsupported BLAKE2b
direct-coinbase settlement rows are individually quarantined and excluded from
credit/payment without starving valid custodial rows. Wallet detail amounts preserve
owned immature credit while Knots reports a zero top-level amount. They do not imply
spendable reserves. Missing or contradictory wallet/header evidence triggers one
family-specific alert after 30 minutes per continuous episode; verified reconciliation
clears it. A process-scoped binding survives payout-handler recreation and configuration
cycles, refusing chain/schedule changes until the documented stop/restart. Expected-work
RPC expected-work models are test fixtures; production ignores those optional
extensions instead of consuming them in legacy or BLAKE2b accounting.

## Release verification

Use the upstream [release manifest and signatures](https://bitcoinknots.org/files/29.x/29.4.2.knots20260508/).
The reviewed archive SHA-256 values are:

| Archive | SHA-256 |
| --- | --- |
| `bitcoin-29.4.2.knots20260508-x86_64-linux-gnu.tar.gz` | SHA-256 `b59d0445a317e21a03dc29425db3aba79b27d5125230b1a2b1dce62e120827c5` |
| `bitcoin-29.4.2.knots20260508-win64-pgpverifiable.zip` | SHA-256 `8fa3445a0f3ecc7d1f9e4f4778e44c786883437ac781902a38135be5ea0a892b` |
| `bitcoin-29.4.2.knots20260508.tar.gz` (released source) | SHA-256 `11c0b99a82b8b1c9c29ab76d9b0507ce1017813665741627b3f3883a4c2f7a7f` |

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

**Complete the Knots and Miningcore upgrade before mainnet height 973440.**
At or beyond enforcement, 29.4.1 templates may include coinbase spends rejected by
29.4.2 consensus. If already past the boundary, keep admission stopped and complete
revalidation, synchronization and reconciliation before resuming.

1. Close new mining/PPS/payment admission and perform the documented graceful drain.
   Plan this before the deadline: pause new wallet broadcasts and verify every
   already-broadcast payout has confirmed on the validated chain before stopping
   29.4.1. Record its txid, consumed coinbases, recipients and durable batch outcome.
   A 29.4.2 restart re-locks **all** coinbases with 101Ã¢â‚¬â€œ6480 confirmations; its mempool
   rejects their spends even when they were spendable under 29.4.1. Unconfirmed
   payouts may be evicted or not relayed by reviewed peers, and their change may
   become unavailable. A recorded payment/txid does not prove confirmation.
   If confirmation cannot be obtained safely before enforcement, keep admission
   stopped, preserve the unresolved outcomes and reconcile them after upgrading;
   do not delay the required consensus upgrade or rebroadcast a replacement as a
   second payout without resolving the original transaction through the durable path.
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
   using the dedicated wallet's `getbalances` after synchronization/indexing.
   Blocks credited as Confirmed under 29.4.1 can lose their liquid backing at upgrade
   until their coinbases reach 6481 confirmations. Keep those credited balances and
   PPS liabilities; fund them with independently verified mature reserves. Check
   prior payout confirmations/conflicts and change outputs before resuming. Chainstate
   revalidation can also invalidate prior observations, so a pre-upgrade confirmation
   is evidence to reconcile, rather than permission to skip this check.
   Follow the [PPS reserve policy](pps.md). The long wallet lock survives consensus
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

CI caches the release archive by its full source SHA-256, rechecks its bytes on
every use and checks both functional-script hashes before execution. New downloads
are verified before atomic cache publication. Failures retain the temporary framework
and daemon logs, print their last 200 lines and upload diagnostics with seven-day
retention. Successful runs remove only their own temporary fixture. The offline
`python3 scripts/regtest/test-knots-maturity-gate.py` checks cache-hit verification,
corrupt-cache rejection, failed-download cleanup and failed-test log retention.

Miningcore's `30:134:140` fixture additionally checks header-v2 GBT rule transitions,
pre-coverage wallet rewards, ordinary non-coinbase spending, confirmation 110 versus
111, restart/reorg/reconsideration, contract outages/drift and a downward reorg between
wallet and chain reads. The `2:201:202` fixture verifies insufficient mature wallet
funds do not create payment records. Production-container startup verifies removed
`getdifficulty`, old SHA256d header fields, new next-block expected work and unchanged
target-derived job difficulty. Existing tests exercise all four ASIC layouts, real
Stratum proofs/accepted blocks and PostgreSQL exactly-once credit, payout schemes,
conflicting replay rejection and preserved PPS liabilities after orphaning.

Initial validation, before the review hardening below, on 2026-10-04 used the documented Windows/Ubuntu 22.04 WSL lab,
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

## Review hardening

The follow-up addresses both supplied reviews, including their lower-priority findings:

Final follow-up validation in the same documented lab passed **3604 Linux tests,
zero failures, one skipped benchmark**, including all real-daemon, native,
PostgreSQL accounting and authentication/TLS/recovery checks. Unchanged native
libraries were built from source; a complete SDK managed rebuild reused their
checksum-verified outputs with the CI native/database environment settings.
The Windows live/compatibility/ledger run passed **623 tests, zero failures/skips**;
the final diagnostic/redaction run passed **76 tests, zero failures/skips**.
Both unmodified upstream functional tests passed on cold and warm source-cache
runs. All five offline cache/failure checks, documentation gates/links, workflow
action pins, diagnostic source guards, Bash syntax/ShellCheck and whitespace checks
passed. The Windows toolset and pre-existing full-suite TLS limitation above still
apply; this evidence does not claim a passing full Windows suite.

| Findings | Change and regression evidence |
| --- | --- |
| P1, L2: pruned rewards and full-block RPC cost | Header-only reconciliation; real `-prune=1 -fastprune` fixture proves `getblock` fails for a retained coinbase header, preserves immature credit, matures it and reconciles invalidate/reconsider without its body. |
| P2: stale catalogue source | Catalogue points at `58398baf...`; configuration test pins source and protocol together. |
| P3, L1: misleading/missing delayed alerts | Dedicated family-specific, redacted, bounded alerts cover missing/contradictory header or wallet evidence, including wallet -5 and absent details. Tests cover active/inactive/unavailable states, recovery and handler recreation. |
| M1: user-agent customization | Recognized prefix supports comments/appended fragments; malformed prefixes, control characters, unknown versions and release candidates remain refused. Unit cases and actual node startup/payout cover the supported options. |
| M2: zero immature reward | Owned `details[].amount` credit survives a zero top-level wallet amount. Live tests assert the pending value; malformed, duplicate, mixed-category and overflow output evidence stays pending. |
| M3: upgrade deadline | Upgrade before 973440; successor-review planning before proposed divergence at 979920 is explicit in the runbook/release notes. |
| L3: one unsupported row stops the pool | Quarantine direct-settlement rows individually; preserve legacy submission metadata and continue healthy custodial reconciliation without crediting the unsupported row. |
| L4: shared strict RPC extensions | Test-fixture response types isolate expected-work conversion; production Bitcoin-family and BLAKE2b DTOs ignore these unrelated optional extensions. |
| L5: regtest parser mismatch | Constraints match released `chainparams.cpp`, including enforce >= 2 and enforce-before-start; loader tests and a real distinct-boundary schedule verify both. |
| L6: coverage/fixture gaps | Added all grace/mismatch cases, PoW/PoS startup batch positions, customized agents and production payout-handler selection. Unscheduled regtest mocks now omit the deployment as Knots does. |
| L7: CI transport/diagnostic fragility | Checksum-keyed caching, warm/cold real-daemon gates and five offline cache/diagnostic checks. |
| Other review notes | Dedicated payout subclass, process-scoped chain/schedule binding across configuration cycles, runtime payout exceptions, and removal of the unused consensus-depth helper. Consensus boundaries remain tested by the unmodified upstream gate. |

## Re-review hardening

Final re-review validation in the same documented lab passed **3635 Linux tests,
zero failures, one skipped benchmark**, and **730 Windows live/compatibility/ledger/
diagnostic tests, zero failures/skips**. The five maturity integration tests passed
independently, including the new persisted lifecycle across all four schemes;
254 focused transaction/recovery/diagnostic tests also passed. The final documentation,
workflow-pin, diagnostic-boundary, offline cache and shell/whitespace gates passed.
Native source and upstream functional scripts/binaries were unchanged; the Linux
run reused checksum-verified native outputs through the complete SDK managed build.
The previously documented Windows full-suite TLS limitation still applies.

The second re-review identified a persistence gap: handler-only invalidate/reconsider
tests did not prove recovery after Orphaned had committed. Production now reloads
custodial BLAKE2b orphans in keyset batches of 64 within 12960 blocks of the observed
tip, advancing and wrapping on every normal payout cycle. This avoids repeatedly
examining an unavailable oldest prefix. The cursor resets at process restart;
persisted candidate/status evidence remains authoritative. No schema migration or
direct-settlement schema dependency is introduced.

An orphan stays orphaned on unavailable evidence. Reopening requires a matching
active header, wallet coinbase and owned reward credit. The row-lock commit verifies
unchanged pool/row ID, height, hash, coinbase txid, miner, creation time, source,
network difficulty and candidate type/settlement mode. Confirmed custodial rows are
excluded from both selection and commit, preventing stale replay from allocating
the reward twice. Their booked balances/PPS liabilities are not reversed.

The real Knots/PostgreSQL test exercises all four schemes through production
`PayoutManager`, repository queries, row locks, block commits and balance allocation:
Pending -> persisted Orphaned -> Pending, then persisted Orphaned -> Confirmed.
It recreates managers/handlers, never resets the stored Status, preserves PPS
liabilities, and rejects a stale confirmed classification without extra balance
changes or wallet payments. Separate tests cover bounded cursor wrap, immutable
evidence changes, unavailable orphan evidence and already-confirmed replay refusal.

| Re-review finding | Resolution |
| --- | --- |
| P1: persisted orphan/reactivation hole | Bounded typed-family selection and immutable row-lock admission, with fresh active wallet/header evidence and real persisted lifecycle tests for SOLO/PROP/PPLNS/PPS. |
| P3: wallet-category log mismatch | Success logs include the reviewed `immature`/`generate` category; captured-log tests verify both. |
| N1: upgrade re-locks prior payouts/liquidity | Runbook requires planned broadcast drain/confirmation before stopping 29.4.1, preserves unresolved outcomes if the deadline prevents confirmation, and checks `getbalances`, previous payouts/change and liabilities after upgrade. Pinned wallet/mempool source establishes the all-coinbase policy. |
| N2: indistinguishable payout-attestation stalls | Fixed diagnostic codes 701Ã¢â‚¬â€œ704 and bounded redacted alerts distinguish RPC availability, syncing, contract drift and binding mismatch; tests cover recreation, recovery, transport/parse errors and cancellation. |
| N3: unknown headers remain unresolved | Operator guide explains why -5 cannot prove orphaning, the once-per-episode alert policy, verified node/wallet repair/rescan and ledger/payment safeguards; unavailable evidence retains its existing status. |
| Unused expected-work models | Moved to test fixtures and corrected the guide/release notes: production ignores optional expected-work fields and preserves target-derived accounting. |
| User-agent diagnostic redaction | Received daemon subversion is withheld from startup errors as well as logs/alerts; private RPC inspection remains available. Tests reject malicious agents without exposing their payload. |
| Commit explanation and stale PR test counts | The prior restructuring commit has a detailed body; the recovery commit and PR description record the final scope and current validation evidence. |

## Third review hardening

Both third-round reviews were evaluated against head `61b306781c04c61711f7c84195562c2e538a70f1`.
The previous automated review summary covered `75dc616112e1ffe867d3133f14bd03bdb72bfab0`;
it is historical evidence, not an automated review of the subsequent heads.
This section records the implemented findings and their regression coverage.

Final third-round validation in the documented Windows/Ubuntu 22.04 WSL lab:

- Windows live/ledger/Bitcoin compatibility/RPC diagnostic suite: **805 passed,
  zero failures/skips**, using the documented local v142 toolset.
- Full Linux suite: **3662 passed, one failed, one skipped benchmark** (3664 total).
  The sole failure was the unchanged Stratum test
  `RunAsync_LocalAdmissionClosureRejectsQuietlyWithoutLeakingConnections(false)`
  rebinding its socket with `Address already in use`; both variants passed on
  isolated retry (**2 passed, zero failures/skips**). This is not recorded as a
  zero-failure full lab run. Every new RPC/history test passed in the full suite.
- The two new live historical-recovery/query tests passed independently on Windows,
  covering actual later PROP/PPLNS allocation and deleted earlier shares, stored
  Orphaned and reopened Pending rows, repeated recovery and handler/manager recreation.
- Complete Linux SDK rebuild passed without warnings/errors and reused
  checksum-verified unchanged native libraries. Native algorithms, real Knots,
  Core/Litecoin/Dogecoin, PostgreSQL and isolated authentication/TLS/recovery faults
  remain included. The prior full-Windows TLS fingerprint limitation still applies.
- Documentation/link, workflow-pin, diagnostic-source, five offline upstream-cache,
  shell and whitespace gates passed. The pinned upstream functional scripts and
  binaries are unchanged from the previously recorded cold/warm successful runs.

The new exact PR head must also pass CI; previous-head CI and the historical
automated review summary do not establish validation of subsequent changes.

| Third-round finding | Resolution |
| --- | --- |
| P3: production RpcClient wraps contract failures as -500 | Inspect structural `JsonRpcError.InnerException`; decoded incompatible results/envelopes and missing mandatory methods (-32601) produce 703 ContractDrift and an immediate bounded alert. Null/scalar/array results and malformed error-code fields are covered. Raw RPC messages are never inspected or emitted. |
| R3-1: stale orphan misses generate misleading pending alerts | Unavailable headers for already-stored orphans clear the delayed episode and emit only fixed/numeric Debug diagnostics. The orphan remains stored. Tests advance the clock beyond 30 minutes and recreate the handler; active headers with absent wallet proof still alert and say unresolved. |
| R3-2: recovered PROP/PPLNS rewards can reuse consumed allocation history | Under the immutable row lock and database-wide payout lease, confirmation is withheld if another ordinary custodial Confirmed row has a later/equal Created timestamp. The guard includes reopened Pending rows after restart. It retains stored status/reward, applies no allocation and sends one immediate per-block process-lifetime alert through the shared tracker. Real Knots/PostgreSQL tests first settle the later reward through the actual PROP/PPLNS schemes and verify older shares were deleted, then verify repeated/recreated recovery produces no extra credit or payment. SOLO/PPS and ordinary recovery remain covered. |
| JSON framing and conversion were conflated | Parse a complete JSON value before RPC-envelope conversion. Framing failures remain JsonReaderException/701; incompatible decoded envelopes are wrapped as JsonSerializationException/703 even when their conversion cause is a reader error. Real HTTP endpoint tests exercise production RpcClient, rather than only fake thrown exceptions. |
| Unchanged Orphaned rows are rewritten every scan | Skip unchanged orphan classifications under the row lock before effort calculation, updates and notifications. Changed persisted financial/progress/effort fields still follow the guarded update. |
| Cursor dictionary concurrency is implicit | Document that ProcessPoolsAsync serializes access; concurrent classification requires synchronization. |
| Legacy-development upgrade hazard and recovery | Operator/release guidance explicitly covers premature orphaning on wallet -5, removed PROP/PPLNS share history and recovered funds already swept. Recovery requires original allocation/payment evidence and spendable backing; forcing status or rewriting history is not a repair. |
| Merge/review evidence can become stale | Recheck official Knots release/source and proposals, publish validation for the new exact PR head, and retain the immediately-before-merge gate. Do not treat old automated review text as current-head approval. |

The framing/conversion distinction follows the official
[JsonReaderException contract](https://www.newtonsoft.com/json/help/html/t_newtonsoft_json_jsonreaderexception.htm)
and [JsonSerializationException contract](https://www.newtonsoft.com/json/help/html/T_Newtonsoft_Json_JsonSerializationException.htm),
verified against production HTTP deserialization. The history query also has a real
PostgreSQL test excluding older/self rows, other pools, nonconfirmed status and
direct/auxiliary types without requiring optional direct-settlement columns.
Equal creation timestamps are conservatively treated as ambiguous allocation order.
The guard does not certify that retained shares are complete when no later row exists;
historical recovery still requires the documented audit and funding check.

## Fourth review hardening

The two fourth-round reviews approved `c2f220a05d0df86427e3d5cd8d89444ee06bd6f9`
with low-priority shared-RPC and held-allocation lifecycle recommendations. On
2026-10-05 the exact reviewed head had **16 successful check runs and one intentional
release-publish skip**. Its [full .NET CI lane](https://github.com/NINJAK1DD/miningcore/actions/runs/37242039578/job/111552536990)
reported **3663 passed, zero failed, one benchmark skip**. The packaged Ubuntu 26.04
lane separately reported 3595 passed and 43 fixture/benchmark skips. These are distinct
runs; the earlier third-round local socket collision remains recorded above.
This CI closure supersedes the old PR description's pending status for `c2f220a05`.
New fourth-round changes require their own exact-head CI before merge.

| Fourth-round finding/recommendation | Implementation and verification |
| --- | --- |
| R4-1: second copy of large RPC result trees | Build the small envelope from parsed fields while borrowing result, structured error data and extension tokens. JToken/JObject callers and batch wrappers return the original token; typed DTOs convert once. Boxed bool/number/string results and primitive error data retain their CLR contracts for shared Ethereum/Xelis consumers; wrong numeric-to-string conversions fail. A parsed two-megabyte transaction fixture asserts reference identity, avoiding the copy rather than relying on a timing threshold. |
| R4-1: HTTP 401 changes non-BLAKE2b failure labels | Empty bodies retain a fixed HttpRequestException cause; non-JSON unsuccessful HTTP bodies also expose a transport cause. Valid HTTP 500 daemon RPC errors retain their code. Real HTTP tests run these through the shared single/batch client and the merged-mining classifier, asserting TransportFailure/RpcError and safe diagnostics. |
| R4-1: divergent batch error handling | Single and batch share complete framing and envelope decoding. Malformed/truncated/trailing JSON remains a framing error; decoded invalid envelopes/error fields are structural serialization errors. Batch count, unique IDs, ordering and response correlation still use the existing guarded path. Scalar strings, typed auxiliary DTOs and null success results remain covered. |
| R4-2: perpetual Pending/Orphaned allocation hold and Warn spam | Commit Quarantined under the immutable row lock and existing payout lease before any financial action. Retain stored reward/effort, persist fresh verified progress, suppress ordinary settlement events and emit the warning/alert only after that committed transition. Pending/orphan scans skip the durable row after restart. Unit tests reject stale terminal-row replay and failed persistence without alerts; real Knots/PostgreSQL tests retain balances/payment history across repeat cycles and recreation after actual later PROP/PPLNS share pruning. |
| R4-2: audited recovery needs a final lifecycle | Keep Quarantined as the automatic terminal state and record the independently approved financial resolution in existing ledger receipts. The documented psql script validates block identity, account/pool/receipt bindings, all case IDs and exact approved total, then adds only a durable case tag. Default dry run rolls back; replay is idempotent and conflicting cases fail. Real psql tests exercise successful and rejected inputs while proving it cannot change balances/payments/block status. |
| Optional notifier abstraction | PayoutManager uses the optional IBlockAllocationHoldNotifier contract instead of a BitcoinBlake2bPayoutHandler cast for post-commit notification. Notification failure cannot undo a committed quarantine. |
| Commit-body suggestion | The user's explicit one-line `type(scope): description` preference takes precedence. Shared-RPC rationale, compatibility impact and validation are recorded in the PR and this document instead of a commit body. |
| Additional diagnostic hardening found by HTTP regression | Merged-mining descriptions previously forwarded raw daemon/client error messages into startup diagnostics and fallback state. Descriptions now expose only fixed categories, timeout values and numeric RPC codes; synthetic private payloads in real HTTP tests must not appear. |

The result-copy decision follows the official [JToken.ToObject contract](https://www.newtonsoft.com/json/help/html/M_Newtonsoft_Json_Linq_JToken_ToObject_1.htm).
The metadata script follows [psql literal-variable quoting](https://www.postgresql.org/docs/15/app-psql.html):
inputs are quoted outside the dollar-quoted procedure and read from a temporary context.
No migration, new credit endpoint, amount write or fabricated share history is introduced.
The [operator closure procedure](bitcoin-blake2b.md#closing-an-audited-allocation-quarantine)
retains the independent entitlement, existing-payment and spendable-backing audit.

Final fourth-round validation in the documented Windows/Ubuntu 22.04 WSL lab
(2026-10-05):

- **855 selected Windows live/ledger/Bitcoin compatibility/shared-RPC/merged-mining
  diagnostic tests passed, zero failures/skips**, with the documented local v142 toolset.
- **168 final focused tests passed, zero failures/skips**, including production HTTP
  single/batch/boxed-scalar failures, commit/notification isolation and real psql audit closure.
- Final full Linux suite: **3697 passed, one failed, one benchmark skip** (3699 total).
  The unchanged Stratum test
  `RejectedSecondRun_PreservesReusedReservationsAndDisposesOnlyNewOnes` failed at
  its socket rebind with `Address already in use`, then passed isolated retry
  (**one passed, zero failures/skips**). Every new RPC/quarantine/resolution regression
  passed. This is not claimed as a zero-failure full local run. An earlier fourth-round
  full run, before the final scalar-compatibility additions, passed all 3690 enabled
  tests with one benchmark skip; it does not substitute for final-source validation.
- The complete final Linux SDK rebuild passed with zero warnings/errors and
  checksum-verified unchanged native libraries. Real Knots/Core/Litecoin/Dogecoin,
  native algorithms, PostgreSQL accounting and isolated authentication/TLS/recovery
  faults remained included. The prior full-Windows TLS fingerprint limitation remains
  distinct from the selected Windows suite.
- Documentation/link, workflow-pin, diagnostic-source, five offline cache, shell and
  whitespace gates passed. Both owned disposable PostgreSQL clusters were stopped;
  source/binary/upstream functional-test pins remain unchanged.

Official source/release freshness was rechecked on 2026-10-05: released
`v29.4.2.knots20260508`, `29.x-knots` source
`58398baf33e588779685ead478e6397bb28ed3d6`, proposals #429/#434 still open/unmerged.
Retain the immediately-before-merge recheck and #163 DATUM/direct-reward acceptance gates.

## Fifth review hardening

Both supplied fifth-round reviews examined `d84bcb328`. All actionable findings,
observations and recommendations are addressed below. Earlier review approvals
remain evidence for their reviewed head only.

| Fifth-round finding/recommendation | Implementation and verification |
| --- | --- |
| R5-1: source-only recovery command and packaged link | Move the unchanged metadata-only tool to `scripts/ops/`, include it at that same relative path in both Linux release archives, and keep it outside schema migrations. The runbook defaults to the verified immutable release directory and gives installed/source alternatives. Its relative tool link works from both source and packaged docs. Archive fixtures compare the exact SQL bytes, resolve the documented member/link and reject migration placement. The real psql fixture now executes the relocated production file. |
| R5-2: incomplete RPC errors and success envelopes | Require an integer JSON code representable by the existing Int32 error contract and a string message, without string/numeric/bool coercion or default code zero. A success must contain `result`; an explicit null remains valid. Missing/null/wrong-type fields, fractional/floating/string/overflowing codes and absent results fail as JsonSerializationException in real single and batch HTTP tests. Production BLAKE2b attestation immediately reports bounded 703, with no wallet/financial action or private payload disclosure. |
| O1: trailing JSON compatibility impact | Release notes explain the shared client rejects trailing non-whitespace content for every coin/proxy. Existing framing tests cover both paths; scalar/typed/null success and valid error tests accept final whitespace/newlines. |
| O2: distributed clocks and Created ordering | Document synchronized UTC clocks across Stratum/relay/recorder/payout hosts, offset/step monitoring and recovery after historical skew. The real PostgreSQL history query additionally covers reversed chain-height/Created order while retaining the conservative hold. The CoreDRP handoff requires persisted shared allocation order and share-window identities before any future guard replacement; height alone does not identify consumed shares. |
| Operator tool mistaken for migration | Dedicated `scripts/ops/` source/package placement and fixture separation make the manual audit action explicit. The SQL is byte-for-byte unchanged; no new migration or automatic financial action is introduced. |
| Prior exact-head CI closure | Verified the reviewed `d84bcb328` head completed 16 successful checks and one intentional release-publish skip. Its full [.NET attempt 2](https://github.com/NINJAK1DD/miningcore/actions/runs/37247824230/job/111572794940) passed 3698 tests, zero failed, one benchmark skip. The initial unchanged observer assertion failure and same-commit retry are recorded in PR/issue history; this evidence does not substitute for fifth-round exact-head CI. |

The [JSON-RPC response/error specification](https://www.jsonrpc.org/specification#response_object)
requires a result on success and integer code/string message on failure. The client
retains legacy daemon envelopes containing `error:null` on success or `result:null`
on error; this change does not impose JSON-RPC 2.0 version/mutual-exclusion rules on
older coin families. Codes outside Int32 cannot be represented by JsonRpcError and
therefore fail the existing caller contract. JSON strings stay strings during framing,
including ISO-looking messages/data; typed result dates still convert through Json.NET.
Tests retain bounded error codes, arbitrary data, boxed scalar consumers, typed dates,
batch ID ordering and borrowed large-result tokens.

Fifth-round final lab validation (2026-10-05):

- **899 selected Windows tests passed, zero failures/skips**, with the documented
  v142 build toolset and real reviewed Knots/PostgreSQL/psql fixtures.
- **3742 full Linux tests passed, zero failures, one benchmark skip** (3743 total).
  Real Knots/Core/Litecoin/Dogecoin, native hashing and PostgreSQL/authentication/
  TLS/recovery suites remained included. All 44 new RPC cases and the expanded
  clock-order query passed. The prior full-Windows TLS fingerprint limitation
  remains distinct from this selected Windows suite.
- The complete Linux SDK rebuild passed with zero warnings/errors; reused unchanged
  native libraries were checksum-verified. Both Ubuntu archive fixtures validated
  operator SQL bytes/location, packaged runbook link and migration exclusion.
- Documentation/link, workflow-pin, diagnostic-source, five offline cache, shell
  and whitespace gates passed. Both owned disposable databases were stopped.
  Source/binary/upstream functional-test pins remain unchanged.

Exact-head CI is recorded on PR #207 and Issue #205 after publication; previous-head
CI/reviews remain historical evidence. Official release/source was rechecked on
2026-10-05: `v29.4.2.knots20260508`, source
`58398baf33e588779685ead478e6397bb28ed3d6`, #429/#434 open/unmerged. Retain the
immediately-before-merge upstream and #163 DATUM/direct-reward acceptance gates.

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

Both proposals target changed rules beginning at height 979920. Monitor released
updates **before that height** and budget another source/build/RPC review and Miningcore
compatibility release before adopting any successor daemon: the existing pin does not
auto-accept future builds. Their proposed timing is not an installed rule or a promised
calendar date; if no successor has shipped, the reviewed 29.4.2 contract remains the
only supported baseline.
