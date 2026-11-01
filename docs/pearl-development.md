# Pearl (PRL): deployment, protocol and validation

Pearl uses its own Miningcore family (`pearl`) and **PearlHash salted integer V3** proof verification. It is not a nonce hasher. The pool verifies plain matrix proofs, durably records shares and candidate work, generates a certificate in an isolated worker, submits the exact completed block, and pays matured custodial rewards through Oyster.

For step-by-step setup, enabling PRL, miner commands, official-client access and troubleshooting, use the [pool operator guide](pearl-pool-guide.md).

## Supported baseline

| Component | Certified scope |
| --- | --- |
| Consensus/node/wallet | Pearl **v1.5.5**, commit `d7c517d267f10f5bcbbdc366f3e603942f9a5ab5`; source-built `xmss,zkpow` node and Oyster |
| Mining | Current dense integer V3; pool admission limited to m<=32772, n<=15360, k=32768, rank=128, tile 8x16 within the certified prover budget; mainnet and proof-enabled regtest |
| Miner protocol | SRBMiner-MULTI **3.7.3**, named-object `type: v2` dialect, tested on Linux with RTX 3080 Ti, default dense profile (m=32772, n=15360, k=32768, rank=128, tile 8x16) |
| Second miner | BzMiner **v100.60**, CPU path, m=n=1024, k=32768, rank=128, tile 8x16; explicit environment settings in the operator guide |
| Official client | Pinned upstream `MiningClient`, `MiningJob`, `PlainProof` through the private Unix-socket adapter and verified TLS; exact integer pool target and final durable share decisions |
| Pool/helper package | Source-built **Ubuntu 26.04 glibc x64**, .NET 10, Rust/Cargo **1.93.1** |
| Reward schemes | PPS plus custodial SOLO, PPLNS and PROP; all other schemes rejected |
| Unsupported | Historical mining, MoE, draft FP8, NoxChain merge mining, testnet/testnet2 pool operation, musl, ARM64, Windows and macOS workers; Ubuntu 22.04 packages are not certified for Pearl |

Only the named releases and profiles are advertised. BzMiner's GPU defaults on the tested GPU used k=65536 and exceeded the bounded certificate prover; that GPU path is not certified. WildRig 0.51.8 documents its Pearl mode as pool-specific and its handshake is incompatible. No ARC/RGMiner/Fl4shMiner/PeakMiner interoperability is claimed. Other mining families and their existing transport limits remain independent.

[Official node/proof source](https://github.com/pearl-research-labs/pearl/tree/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5), [release](https://github.com/pearl-research-labs/pearl/releases/tag/v1.5.5), [SRBMiner release](https://github.com/doktor83/SRBMiner-Multi/releases/tag/3.7.3).

## Build and install

Build Miningcore using the normal Linux native build instructions. Install the reviewed Rust 1.93.1 toolchain, then:

```bash
bash scripts/pearl/build-worker.sh /absolute/build/pearl
python3 scripts/pearl/test-worker.py /absolute/build/pearl/output/miningcore-pearl-worker --prove
dotnet publish src/Miningcore -c Release -o /absolute/publish \
  -p:PearlWorkerBundle=/absolute/build/pearl/output
```

The publish target validates checksums, licenses and the worker version and installs `pearl/miningcore-pearl-worker` plus its notices. The Linux archive packager preserves its executable bit and rechecks the bundle. The optional `PearlWorkerBundle` property never downloads or silently chooses another consensus implementation. A pool configured without an installed, matching worker fails startup. Ordinary releases without this optional bundle cannot run Pearl until the source-built bundle is installed with `scripts/pearl/install-worker.sh BUNDLE PUBLISH_DIRECTORY`.

The helper build checks the exact clean upstream revision, uses both Cargo lockfiles, generates and embeds V1/V2 circuit caches, records cache/binary checksums and toolchain versions, and includes the upstream license inventory and Cargo dependency license/notice files. Keep the whole `pearl/` directory when distributing binaries. Do not replace its worker with an unreviewed executable that merely prints the expected version.

Stop all database writers, back up the database, and apply the ordinary share-accounting migrations plus `migrations/add_pearl_candidates.sql` as the schema owner. New installations use `createdb.sql`. The application role needs the usual table DML privileges, not PostgreSQL superuser/CREATEDB privileges. The pool checks for durable share-accounting and candidate schemas before accepting miners.

## Node, Oyster and pool configuration

Start with [examples/pearl_pool.json](../examples/pearl_pool.json). Replace every `CHANGE_ME` value. Keep the pool configuration and wallet credentials readable only by the service user. `proofWorker` must be an absolute path. Use a separate data directory for each network, and verify genesis before opening a miner port.

Build or install the exact official node/wallet release with real ZK verification enabled. Follow the pinned [node installation instructions](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/node/docs/installation.md) and [Oyster guide](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/wallet/README.md). `task build:blockchain` builds the official programs; inspect the build tags and run the proof-enabled lab checks before trusting a custom build. Simnet/bypass/stub builds do not certify this integration.

Mainnet ports are node RPC **44107**, P2P **44108**, Oyster RPC **44207**. Configure Oyster's full-node connection to the same synchronized node; do not leave it pointed at a different network or an unrelated SPV backend. Use Oyster `getnewaddress` on the `default` BIP86 account to create a wallet-owned `prl1p...` reward address. Back up the wallet seed/database before mining. Configure node RPC with credentials permitted to call GBT/submitblock, and a separate `category: wallet` endpoint for Oyster. Startup checks both genesis values and Oyster ownership of the reward address.

Expose only the public Stratum TLS ports. Bind RPC, PostgreSQL, admin and metrics endpoints to loopback or a restricted management network. For same-host plaintext RPC, explicitly bind the daemons to loopback and use their local TLS-disable options; for remote RPC set `ssl: true`, install the issuing CA in the host trust store, and use a hostname matching the certificate. Miningcore verifies the certificate chain and hostname and refuses redirects. Never disable certificate verification to make a remote wallet work.

The example has public TLS ports 4400/4401 with an operator PFX certificate. On a trusted lab network only, a separate loopback non-TLS listener can be used. The tested miner invocation was:

```bash
./SRBMiner-MULTI --algorithm pearlhash --pool 127.0.0.1:4400 \
  --wallet rprl1pYOUR_LAB_ADDRESS.rig1 --password x --disable-cpu --gpu-id 0
```

For production add `--tls true`, use the configured public endpoint and a mainnet `prl1p` address. Do not reuse lab wallets or RPC passwords. The official miner archive is retained for repeatable testing; SHA-256 `c7250d39fe23549a88ef5d8d2041d75e60166f5d6ab8c8daf7274ab7b0a8da20`. BzMiner v100.60's official Linux archive SHA-256 is `1d736e08189a8bb0a5ebe1b1e1b51509b86af4aeea21337b8f799924d46533c5`, verified against its [release](https://github.com/bzminer/bzminer/releases/tag/v100.60).

## Pool protocol contract: named-object v2

Messages are newline-delimited JSON-RPC. IDs are required; string IDs are limited to 64 characters. No positional Bitcoin subscribe/configure dialect is negotiated. A client explicitly selects this dialect in authorization:

```json
{"id":1,"method":"mining.authorize","params":{"wallet":"prl1pADDRESS.rig1","agent":"SRBMiner-MULTI/3.7.3","type":"v2"}}
```

The address must be a network-correct, checksum-valid Bech32m witness-v1 32-byte program. Uppercase addresses are normalized; mixed case is rejected. Worker names are optional, at most 64 ASCII letters/digits/underscore/hyphen. Wallet strings are bounded at 160 and agent strings at 128 characters. Reauthorization is rejected. Authorization is an accounting identity, not proof that the client owns the payout key.

BzMiner v100.60 supplies the worker in a separate string-valued `worker` parameter.
Both that field and `wallet.worker` are supported with identical validation. If
both are present they must agree; conflicts, non-string values and overlong names
are rejected rather than silently crediting a different worker.

For this miner the first `mining.notify` precedes the authorization acknowledgement (`result: true, error: null`). Notify `params` is an object containing `job_id` (32 hex characters), `header` (76 wire bytes as lowercase hex), `target` (32-byte **big-endian exact integer**, hex), `height`, and `cert_version: 3`. Subsequent target/transaction/time updates create a new job/header and are published under the existing per-connection assignment gate.

```json
{"id":2,"method":"mining.submit","params":{"job_id":"JOB_ID","plain_proof":"BASE64"}}
```

Raw base64 and bounded gzip/zlib proof encodings are accepted. There is no client-selected header, reward address, target, certificate version or credited work in submission. Successful shares receive `result: true, error: null` **after** the database receipt and any candidate intent commit. Codes: 21 unknown/stale job, 22 duplicate proof, 24 unauthorized, 20 unsupported/invalid/busy or capacity refusal. A busy refusal does not promise that retrying the same bytes will be accepted; request/use fresh work. Failed native service execution returns a service error without crediting unverified work or penalizing the miner; the failed attempt identity is released. Unknown methods are rejected. On synchronization loss or unsupported upgrades the pool retires work and disconnects miners; miners reconnect when valid templates return.

Jobs are session-local, randomize the coinbase namespace, retain at most 8 snapshots by default (2–16 configurable), and expire after 120 seconds (30–180 configurable). Previous difficulty is accepted only through the configured 0–30 second grace. A changed previous-block hash immediately invalidates an old job. Same-tip transaction/time changes retain immutable old snapshots within those bounds.

## Consensus, targets and financial units

Headers are 76 bytes without a proof commitment and 108 bytes with it. Block identity is SHA256d(header108); the commitment is SHA256d(version LE32 || public data164). A block begins with its certificate, then header, CompactSize count and witness-aware transactions. GBT's witness commitment is a raw 32-byte hash: Miningcore recomputes it and adds the `6a24aa21a9ed` script prefix. It builds the BIP34 coinbase, preserves transaction order/IDs, and reserves certificate space within the block limits.

The worker calls the official wire decoder rather than overlaying Rust's differently ordered in-memory hash fields. Official source handles the 52-byte configuration, BLAKE3 commitments, integer noise/transcript, V3 salted seed binding, Merkle openings and jackpot. Managed code independently validates dimensions, public-data binding, targets and candidate eligibility; preflight is never treated as cryptographic verification.

Let `D1 = DecodeCompact(0x1b00ffff)`, the Pearl mainnet difficulty-one reference. An assigned difficulty `d` produces `floor(D1/d)`, dividing by the exact IEEE-754 value of `d` with integer arithmetic. No compact share override or approximate floating-point acceptance boundary is used. For `L=k-k%rank`, sampled tile size `T=rows*columns`, active work factor `F=T*(L/rank)*128`, accept iff the verified little-endian jackpot is <= `assigned_target*F`. Pool overflow is rejected; consensus candidate scaling saturates at uint256 max. Network bits remain unchanged in the committed header.

`NetworkDifficulty = D1/network_target`; reported achieved `ShareDifficulty` and `ActualDifficulty` are `D1*F/max(1,jackpot)`. Financial `Difficulty` is the assigned difficulty capped at network difficulty, never a client-declared rate or lucky achieved value. Equal assigned difficulty therefore buys equal expected normalized work across the allowed dimensions/ranks. API hashrate is credited difficulty/second multiplied by `2^256/D1`: **rank-adjusted work units per second**, not a promise to reproduce a miner's displayed raw matrix TH/s. Regtest's much easier network target caps credit and can make its dashboard rate much lower than the GPU display.

The structural verifier bounds are `m,n <=2^24`, `k=1024..65536` in multiples of 64, power-of-two rank `128..1024`, `16*rank <= k <=4*rank^2`, 32–256 sampled products, and <=4 MiB sampled matrix input. **Pool admission is narrower:** m<=32772, n<=15360, k=32768, rank=128, tile 8x16, the certificate profile tested within the shipped worker's fixed resource budget. Other profiles are rejected before native verification, share accounting or candidate enqueue, even when consensus-valid. This prevents cheaply verified but unprovable candidates from exhausting the durable queue. The general verifier/rank-penalty contract remains independently tested. The maximum structural scaling factor is `2^24`; difficulty is constrained to `1e-6..1e60`. Verified header/public-data SHA256 identifies work; the first 128 bits identify the existing durable accounting receipt. A conflicting receipt payload fails closed. Alternative proof encodings cannot earn another credit.

Mainnet activation fixtures cover immediately before/at/after heights **71935 (V2), 91630 (dense), 96251 (rank penalty), 99000 (salted V3)**. Historical serialization/rule selection is covered by node-validated blocks and independent official Go vectors. The current verifier deliberately cannot mine historical MoE/low-rank work. Unknown certificate versions and future FP proposals require a new reviewed protocol/activation update; draft PIP-3's reused “V3” label is not negotiated as current integer V3.

## Resource and process boundaries

- 32 KiB before authorization; approximately 11.2 MiB proof JSON frame after authorization. Frame-aware pipe backpressure and 16 KiB reads prevent the large-frame deadlock.
- At most **16 Pearl connections per process**, across pools. Existing global/IP listener admission also applies. Unauthenticated sessions are retired after 10 seconds; authenticated partial frames have a 30-second deadline. Per-connection byte refill is 2 MiB/s with one bounded frame burst.
- JSON depth 8, 128 tokens, numeric/property tokens <=64 bytes, duplicate object keys rejected. Decode output <=8 MiB. Proof structure/count/index preflight precedes Rust allocation.
- One proof attempt/second/worker, at most 256 attempts and verified identities per retained job. Two decoding/verification slots and one proving slot per process; no unbounded native wait queue.
- Verify deadline 5 seconds, native verify address space 1 GiB/CPU 20 seconds. Prove deadline 180 seconds, address space 8 GiB/CPU 600 seconds, four Rayon threads. These are hard ceilings, not expected RSS.
- One request per helper; embedded circuit caches die with the process. Cancellation kills/reaps the process tree. Linux parent-death SIGKILL prevents orphaned helpers after a pool crash.
- Global durable candidate capacity is **16 per database**, enforced under a transaction advisory lock. Only network candidates occupy it. Receipt and candidate insertion are atomic. Capacity refusal does not acknowledge unrecorded work.

Use a dedicated pool service account, no wallet filesystem/private-key access for the helper, and service-level memory/CPU/process ceilings appropriate to the host. Allow headroom for two verifiers plus one prover and the managed transport; a separate node host or at least 24–32 GiB host RAM is prudent for the configured worst-case ceilings. The initial soak measured the pool separately from node/prover/GPU processes. The follow-up benchmark measured the helper itself at roughly **7.96 GiB peak RSS**; do not size the service from managed-pool RSS alone.

The binary IPC is version 2: `PRLJOB02`, operation byte (0 verify / 1 prove), certificate byte 3, two reserved zeros, header76, big-endian target32, LE32 proof length, proof, EOF. Success: `PRLRES02`, status0, candidate byte, two zeros, public data164, LE32 certificate length, certificate <=60000 bytes. Invalid requests/proofs return exactly `PRLRES02`, status **1**, three zero bytes, and exit **1**. Native/system failures return status **2**, three zeros, and exit **2**; panics always use status 2, even during input validation. Signals, missing/truncated/oversized responses, mismatched exit/status and broken pipes are infrastructure failures. The pool does not penalize a miner for those failures and releases the attempt identity so bounded retries can succeed after recovery. Invalid proof attempts remain replay-protected. Version 1 helpers are rejected at startup; upgrade the pool and complete helper bundle together.

This is stream IPC, not an in-process FFI: there are no exported pointers, C structs, calling-convention assumptions, cross-runtime allocator ownership or pointer-width-dependent fields. All widths and endianness above are explicit and there is no implicit padding. Only Linux x64/glibc is certified. The parent owns request/response byte arrays; the child owns all Rust allocations until process exit. Capabilities are the two operation bytes and dense V3 certificate byte; reserved fields, versions, trailing bytes and unknown operations fail closed. Rust panics/errors never return input-derived diagnostic strings. Managed callers bound stdout/stderr and validate version and every output size. Rust panic injection, managed cancellation/concurrent-capacity tests, and an actual parent-death test exercise lifetime safety.

## Candidates, payouts and recovery

An acknowledged candidate survives restart in `pearl_candidates`. Proving uses its exact saved header, transactions and proof, cancels on tip/sync retirement, and never replaces it with the latest template. The complete certificate-first block is persisted before submission. A null submit result, duplicate, or lost response is reconciled by exact `getblockheader` identity. Explicit reject strings do not count as acceptance. Uncertain submissions retain their bytes and rotate through bounded retries; a proven competing canonical block makes the candidate an orphan. Row-locked completion inserts the block exactly once.

Rewards come from GBT, not a fixed subsidy. Classification checks canonical block hash, exact coinbase identity/output, wallet sync and both node/wallet confirmations. Minimum payout confirmation policy is **101** (never below the 100-block maturity boundary). Wrong-network addresses and non-Taproot payout destinations are rejected. One PRL is 100,000,000 grains. Minimum payment is conservatively 0.01 PRL to avoid dust. Payments floor each selected balance to whole grains and retain residual fractions; batches contain at most 100 recipients. Fees are configured in **grains/kB**, converted to Oyster's required third positional `sendmany` argument: `["default", amounts, feeRatePRLPerKb, 1]`. Comments and `getwalletinfo` are not used. The handler unlocks for 60 seconds when configured and always attempts to relock with a bounded cleanup token.

Oyster v1.5.5 cannot sign arbitrary raw transactions through `signrawtransaction`. Therefore every batch first persists an immutable intent with a **fresh wallet-owned 0.01 PRL marker output**. This is change owned by the pool wallet, not a miner fee. The wallet needs spendable headroom for the marker and transaction fee. After the intent exists, an error never authorizes another `sendmany`. Recovery searches that unique marker, verifies the transaction ID and every intended output against raw bytes, requires wallet ownership of every extra change output, bounds the fee, requires node mempool/chain evidence, and idempotently persists the payment batch. If needed it rebroadcasts only those same verified transaction bytes. Missing/ambiguous/conflicting evidence retains the intent for operator reconciliation.

A lost wallet response deliberately stops the payout manager through its existing uncertain-outcome lease policy. Follow [database ownership recovery](database.md): stop the recorded owner, inspect the exact owner/generation and wallet/marker transaction, then release only that dead owner's lease under the prescribed advisory lock. Never delete an intent or mark a payment paid merely to unblock the service. A crash before wallet submission can leave an intent with no transaction; manual wallet audit is required before deciding it was never sent.

Pre-maturity reorgs orphan the reward without changing balances; canonical restoration can reopen it with fresh custody evidence. Confirmed rewards that leave the canonical chain are **quarantined**, holding new payouts for the pool. Already-booked or paid money is never automatically reversed or paid again. PROP/PPLNS out-of-order historical rewards are also quarantined when a later confirmed block could have consumed their share window. Review the chain, wallet and unchanged ledger before any manual release. A bounded rotating scan revisits 64 confirmed/orphaned blocks per cycle within 4032 blocks of the tip; deeper reorgs require operator review of older history.

PPS snapshots the template reward in grains, capped assigned difficulty, network difficulty,
recipient fee and arithmetic version at share admission. `PearlCandidateStore` commits the
same `PpsShareCredit` and payload hash that `ShareRecorder` reconstructs on replay. The
receipt, statistical share, liability, precision remainder, balance and (when applicable)
candidate intent share one transaction before acknowledgement. Failed insertion or queue
admission creates no credit; subsequent candidate rejection or an orphan cannot revoke a
committed liability. The shared PPS block scheme is a no-op for miner balances, avoiding
double payment at maturity. Reward recipients retain the usual matured-block allocation.
Both shared arithmetic versions are supported with their existing durable cutover rules.
See the [four-scheme operator guide](pearl-pool-guide.md#6-choose-and-configure-a-reward-scheme)
for configuration, reserve funding, rounding, migration and troubleshooting.

The supported dense profiles share the rank-adjusted jackpot multiplier
`8 × 16 × (32768 / 128) × 128 = 4,194,304`; changing the full matrix dimensions
does not change that sampled-work multiplier. The verifier applies it to the
assigned target and independently checks the network candidate threshold. It
cancels from the share/network work ratio, so PPS uses assigned difficulty divided
by network difficulty, capped at one, rather than achieved jackpot difficulty or
advertised GPU throughput. Immutable jobs preserve the original assignment during
VarDiff grace. This contract does not extend to unsupported ranks, tiles or kernels.

The [four-scheme verification report](pearl-reward-schemes-evidence.json) records
5,798 full-suite passes and 568 focused passes, with zero failures. A real miner
produced one accepted block per scheme over verified TLS; each matured and paid
through the production Oyster handler. Exact transaction outputs and ledger
conservation were checked, including PPS precision remainders. Restart preserved
all payments, balances, PPS credits and financial history without a second credit
or payment. Private transcripts are retained separately; only their hashes are public.

Final payout review also requires valid 64-character lowercase hexadecimal hashes
from canonical-block lookups and both wallet/node tip lookups. A null, empty or
malformed RPC result cannot establish a competing block or synchronized wallets.
The HTTP/PostgreSQL regressions reproduce both failures against the pre-fix code,
then verify unchanged reward status and balances until valid evidence returns.
Existing durable-intent reconciliation still uses exact transaction identity and
does not authorize another wallet send.

Back up PostgreSQL, its recovery journal and Oyster's wallet together. Run one active pool instance per pool ID; the existing database-wide payout-owner lease still applies. Do not concurrently migrate schemas or restart a regtest node with production wallet data: this upstream regtest node rebuilds its chain on startup, and Oyster must rescan that changed chain.

## Monitoring and troubleshooting

The pool emits periodic health records for readiness, height, certificate/rank policy, live connection capacity and durable candidate pressure. The existing restricted Prometheus endpoint also exports:

| Metric | Labels / interpretation |
| --- | --- |
| `miningcore_pearl_state` | `pool`, finite `measure`: Height (last validated chain height), CertificateVersion (3 when ready, otherwise 0), MinimumRank (128), TemplateAgeSeconds (-1 without work), ActiveJobs, StaleJobs, CandidateQueue, ActiveVerifiers, ActiveProvers, CircuitCaches, PayoutState |
| `miningcore_pearl_events_total` | `pool`, finite `event`: accepted/invalid/stale/duplicate/busy shares; rejected requests/unauthorized workers/native failures; candidate submissions, uncertain attempts, accepted/rejected/retired candidates; pending/mature/orphaned/quarantined reward observations; payout attempt/uncertain/completed/held outcomes |
| `miningcore_pearl_proof_bytes` | `pool`, `phase` Verify/Prove: decoded proof or certificate size histogram |
| `miningcore_pearl_proof_seconds` | Same phase labels: verification attempts (including rejected proofs) and successful certificate wall time |

State gauges are sampled at the template poll interval; CandidateQueue is sampled every 60 seconds. Active/stale jobs count retained session jobs, using the same tip/expiry/difficulty-grace rules as submission. Native slot counts are process-wide, so pool-labelled observations must not be summed across pools. CircuitCaches reports reserved cache capacity (one per active prover), not internal circuit entries. PayoutState is 0 when reconciled/no pending intent, 1 while an intent or uncertainty remains, and -1 when a preflight/policy failure holds payments. Reward counters count classification observations, not unique blocks; candidate uncertainty counts retry attempts. Busy requests also contribute to generic rejected requests. No miner addresses, job IDs, proof strings or daemon error text become labels.

Alert on template age approaching 45 seconds, pending candidate work lasting a 194-second target interval, repeated slot saturation, rising native failures, quarantined rewards or nonzero payout state. The two verification slots, one prover and 16-candidate bound are deliberate; queue capacity does not guarantee service before a tip changes. `miningcore_hash_computation_time` records `PearlHash.verify`/`PearlHash.prove`; debug proof records contain bounded byte counts and verification time, and certificate logs include proof latency/size. Existing RPC telemetry, share accounting, connection, block maturity and payment metrics remain active. Inspect `pearl_candidates` for oldest `created`/`lastattempt`, and `pearl_payout_intents` for unresolved financial work; do not expose their proof/recipient payloads through a public API.

GBT polls every 5 seconds (1–30 configurable), with per-node 10-second deadlines, up to four genesis-checked nodes, and a 45-second freshness gate. Same-tip full-template fingerprints trigger updates. Unknown mandatory rules, certificate changes and sync failures pause work. The fixed bounded polling interval provides reconnect backoff. A stale mainnet template or repeatedly busy prover is an operating fault, not a reason to relax proof verification.

Public-input failures use fixed errors and existing ban/admission controls. RPC/prover diagnostics do not print credentials or arbitrary daemon exception bodies. API payment extension projection exposes fee/minimum-confirmation settings and withholds the wallet password. Review limits before increasing miner concurrency; changing the hard maxima requires code/test review.

## Recorded validation — 1 November 2026

Lab: documented Ubuntu WSL test lab, Ryzen 9 5950X, RTX 3080 Ti 12 GiB, 15 GiB system RAM, source-built proof-enabled Pearl/Oyster 1.5.5. PostgreSQL integration tests used a separate disposable cluster; the running pool used a non-superuser role. No mainnet block or mainnet payment is claimed.

Machine-readable measurements, exact hashes and transcript digests are in [pearl-certification.json](pearl-certification.json). The full regression run passed **5,753 tests, zero failures, 24 skipped**. Following the final reward-scheme runtime guard, the focused run passed **379 tests, zero failures, one opt-in GPU fixture skipped**. PPLNS/PROP tests verify assigned-credit weighting despite different achieved proof difficulties. Skipped lanes are not counted as certification.

The source-built Release package passed the 25-library inventory, 241 managed entry points and eight floating-point load/unload modes, plus the 26 packaged-worker contracts. The final published pool used the shipped `pearl` catalogue entry and SRBMiner over **TLS 1.3**. A deliberately dropped successful `submitblock` response was reconciled to one canonical block record. Archive packaging was contract-tested against lab-built output; a pinned-container build or publicly distributed release binary is not claimed.

- Current-mainnet compatibility: a synchronized eight-peer node supplied height **136635**, bits `1754f66b`, V3, **54 transactions**; the captured template builds through the real managed serializer. Twelve node-validated historical blocks pin all four activation boundaries.
- Production runtime crash recovery: SRBMiner's share receipt and candidate were committed, Miningcore was killed before submission and restarted, and exactly one share receipt and accepted height-1 block survived (SHA-256d identifier): `255946f6fb1cd8b79a5bfe8b70a0d8a1eb56215798af89fcd4dee747a519898b`.
- After 103 proof-validated successors, the actual payout handler allocated a 2.5% fee recipient and miner balance. The fault proxy dropped the successful wallet response after broadcast. Restart reconciled **one sendmany and one payment batch**, with exact outputs 80.74103351 and 3148.90030711 PRL, plus the owned 0.01 marker. Transaction SHA-256d identifier `15a140706f84acac49f6516ab29163ee15dde8a34c1c22dd0ac56d7727009569`; fee 0.00002990 PRL.
- Production block SHA-256d identifier `5fd4944928cfd1f16956e06498c3a87da4f6ca98efb9b4d1e5cec554a361d0e2` included that payout transaction and its witness commitment and was accepted by the proof-enabled node.
- A separate 30-minute real-miner run exercised jobs, target updates, valid-share acknowledgements, candidate retirement and repeated accepted blocks. Detailed counts and resource samples are in the checked-in certification evidence. The miner reported 30 accepted shares and one explicit verifier-busy rejection; no stale or invalid-proof rejection appeared. The bounded admission refusal was not credited. Hard limits, malformed/slow input and helper failures are covered separately; a successful soak is not an unlimited-load claim.
- Live `invalidateblock`/`reconsiderblock` tests verified pending→orphaned→pending before maturity, and confirmed→quarantined after settlement with unchanged balances/payment batches. The original chain was restored; quarantine release was a separate audited lab action.
- Native contract suite: **26 cases including real proving**, exact bound-1/bound/bound+1, overflow, malformed input and binding mutations. Typical captured proof verification was about 41 ms and four-thread proof generation about 47 seconds, below Pearl's 194-second target interval.

Run ordinary managed tests with the documented PostgreSQL/native lab lanes. `scripts/pearl/test-worker.py` runs deterministic native fixtures; `--prove` enables the slower real certificate path. `PearlLiveLabTests` is an additional opt-in GPU fixture (`MININGCORE_PEARL_LIVE_LAB=1`, `MININGCORE_PEARL_LAB_ROOT`); it uses a protocol harness and is not substituted for the production-runtime evidence above. The test-only `MiningcoreStratumListenerSmoke` build property cannot publish a pool.

The mainnet compatibility check used a separately synchronized, proof-enabled node. Operational service names, endpoint locations, host details and credentials are retained only in the private lab records. Official miner archives and their checksums are retained for repeatable tests.

For repeatable lost-response tests, place `scripts/pearl/fault-proxy.py --listen-port 19509 --upstream http://127.0.0.1:19501 --method submitblock --evidence /private/lab/new-evidence.json` between a disposable pool and node. For wallet tests use the wallet port and `--method sendmany`. It forwards the caller's authentication and drops exactly one successful reply after upstream completion; it does not log credentials or request bodies. Inspect the durable candidate/intent, exact node/wallet transaction and ledger before restarting or releasing a dead payout-owner lease. Use only isolated regtest funds for these faults.

## Completion checks and interoperability

The earlier implementation evidence is recorded in [pearl-completion-evidence.json](pearl-completion-evidence.json). Its two previously deferred items are completed by the [interoperability report](pearl-interoperability-evidence.json): a second independent miner and a secure mapping for official-client access. Deploying the gateway adapter remains optional for operators who connect supported miners directly; implementing and verifying it is no longer deferred. A stock solo gateway is not advertised as a pool client.

The adapter preserves the official JSON-RPC 2.0 methods, omits the `error` member on success (required by the pinned client), and serializes the target as an exact integer. It binds echoed jobs to immutable per-session upstream assignments; worker identity comes only from a private configuration. Local socket permissions protect access, and remote pool traffic requires hostname-verified TLS. It never returns the solo gateway's early `submitted` result. See the operator guide for installation, limits and failure behavior. Run `python3 -B scripts/pearl/test-gateway-adapter.py` for the automated transport/security contracts; CI runs these alongside the managed suite.

The certified profile's captured network-candidate and share-only plain proofs are each **801,488 bytes**. Their raw-base64 submit frames are **1,068,758 bytes**; re-encoding the same valid proofs gives gzip frames of **9,498/9,502 bytes** and zlib frames of **8,630 bytes**, including one newline and a 32-character job ID. TLS does not change application frame size. These are measurements of the named default profile, not all possible matrix dimensions or a promise about other GPUs/tunings. The parser's wider structural bounds permit consensus-valid work inside hard limits, but other miner profiles require new measurements and certification before being advertised. Encoded JSON, base64, plain proof, nested dimensions, certificate, RPC and native stdout limits remain separate as documented above and enforced in code.

`scripts/pearl/fuzz-worker.py WORKER --output REPORT --benchmark` reproduces a 2,048-case native mutation campaign (seed 244256), real parent-death termination, encoding/frame measurements and 1/2/4/4-thread proving. The managed suite adds 4,096 nested-structure mutations and 256 compressed-input mutations. These finite seeded campaigns supplement deterministic boundary cases; they are not exhaustive fuzz coverage. Both valid fixture classes are checked separately by `test-worker.py`.

The prover uses the official embedded circuit cache, freshly deserialized for each request. Three cache-load measurements were **22.463, 21.552 and 21.473 ms**. No persistent mutable circuit cache survives a helper. Complete proving measured **164.006 s / 99.085 s / 50.907 s / 53.214 s** with 1/2/4/4 Rayon threads respectively; each certificate was **59,560 bytes** and peak RSS was **8,331,056–8,334,428 KiB**. These are wall-clock and process high-water RSS measurements on the documented lab host while the proof-enabled node was also mining; the second four-thread run has warm OS pages but still a fresh circuit cache. The final packaged binary was independently measured again at **46.378 seconds and 8,343,884 KiB peak RSS** after CPU block generation stopped, with its exact digest recorded. Production always selects four threads. Its measured 51–53-second work leaves headroom within the 180-second helper deadline and 194-second chain interval; the 8-GiB address-space ceiling is a failure containment limit, not spare RAM. Larger/different proof configurations are not performance-certified by these results.

The production payout handler is tested through HTTP RPC and a real PostgreSQL schema for omitted/default/overridden extension settings, explicit invalid configuration, grain-exact rounding/residual retention, minimum payout filtering, insufficient funds, wallet locking, default-account/fee arguments and 100-recipient batching. Fault tests recreate the handler with durable state and exercise lost replies, wallet and node outages, conflicting transactions, corrupted or incomplete outputs, failed sends and exact-byte pending-transaction rebroadcast. Missing or partial evidence retains the intent and ledger unchanged; it never authorizes sending a second batch. Earlier full-runtime process-crash and lost-response tests remain recorded in the original certification report. A failed send with no marker transaction requires operator audit; that safe hold is intentional, not automatic retry.

Live Oyster RPC certification used a fresh proof-enabled regtest chain past height 103 and a disposable wallet. `getbalance("default",1)`, wallet-owned address validation, `chainsynced`, equal wallet/node tips, `listunspent`, `listtransactions("*",10,0)`, `gettransaction`, `sendtoaddress`, `sendmany`, unlock and relock all passed. Unknown-account balances and account-specific `listtransactions("default",...)` are rejected: transaction history must use `"*"`. Locked sends fail, and successful sends were independently observed in the node. Oyster reports **positive PRL fees** in these `gettransaction` responses; the handler validates their absolute grain-exact magnitude. The two successful catalogue sends are intentional self-payments with distinct fresh output addresses, not a payout-recovery test.

The independent Python fixture was generated from a proof-enabled node's accepted V3 block using **the actual pinned `pearl_mining` extension and gateway classes**, numpy 2.4.2 and the upstream-locked bitcoin-utils 0.7.3. `scripts/pearl/compare-python-block.py` reproduces full certificate/header/transaction serialization and CompactSize boundaries 1, 252, 253, 65535 and 65536. Repeated-transaction boundary vectors test framing only and are not valid consensus blocks. Managed tests use the same serializer as production candidate submission and compare the complete bytes/hash with those Python results; the single-transaction fixture itself is node-accepted.

## Sources and fixture provenance

- [Official wire/header/certificate](https://github.com/pearl-research-labs/pearl/tree/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/node/wire), [activation and consensus rules](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/node/blockchain/validate.go).
- [Plain-proof verification](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/zk-pow/src/api/verify.rs), [V3 seeds](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/zk-pow/src/api/seed.rs), [rank/target checks](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/zk-pow/src/api/sanity_checks.rs).
- [Oyster RPC handlers](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/wallet/rpc/jsonrpc/methods.go), [wallet request schemas](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/node/btcjson/walletsvrcmds.go).

`scripts/pearl/generate-wire-vectors.go` regenerates synthetic framing vectors with official Go code; their placeholder certificate is not valid consensus evidence. The gzip fixtures contain actual locally mined proofs, with metadata and SHA256s. `verify_capture.rs` independently checks captured work in the pinned Rust crate. `mainnet-activations.json` records node-validated headers/public data and certificate digests; `mainnet-template.json` records the actual current template. None contains private keys or RPC credentials.

Completion validation: **5,766 full-suite passes** (zero failures, 24 explicit skips) and **389 final focused passes** (zero failures, one opt-in skip). The updated Release pool accepted **16 SRBMiner shares and one block** through IPC v2; its live metrics were scraped. The final height gauge was separately compared with the node after its chain-height correction. See the completion report for exact public transaction/block identities, final binary digests and transcript hashes.
