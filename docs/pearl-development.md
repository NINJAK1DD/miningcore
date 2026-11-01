# Pearl development and validation

This branch contains a **development foundation for [issue #244](https://github.com/NINJAK1DD/miningcore/issues/244)**. It does not yet provide an operator-ready Pearl pool. PRL is intentionally absent from the selectable coin catalogue. The production Stratum runtime, durable candidate/share accounting, Oyster payout handler and recovery integration are not implemented. None of the issue's acceptance criteria is complete.

## Reviewed baseline

- Pearl v1.5.5, commit [d7c517d267f10f5bcbbdc366f3e603942f9a5ab5](https://github.com/pearl-research-labs/pearl/tree/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5).
- SRBMiner-MULTI [3.7.3](https://github.com/doktor83/SRBMiner-Multi/releases/tag/3.7.3), Linux archive SHA256 `c7250d39fe23549a88ef5d8d2041d75e60166f5d6ab8c8daf7274ab7b0a8da20`.
- WildRig [0.51.8](https://github.com/andru-kun/wildrig-multi/releases/tag/0.51.8), Linux archive SHA256 `43f4071c743c922f2febf4b8ea44fc9f93dff668f5ffafcc24903dfd78f63771`. Its local probe sent an opaque hexadecimal opening line; interoperability is **not verified**.
- Initial Miningcore base: `6b183751b` on `dev`; subsequently merged `4fe5ebe8e` (FluentValidation 12.2.0).

The currently deployed V3 protocol uses salted integer proofs. Draft floating-point proposals must not be substituted for it.

## Implemented components

- `PearlConsensus`: exact positive uint256 compact targets, hard rounding, explicit saturation versus rejection, 76-byte incomplete headers, and V1/V2/V3 dense certificate-first header framing.
- `PearlPlainProof`: allocation-free validation of every nested dense bincode count, dimension, tree size and index before Rust deserialization. It does not claim to validate a proof cryptographically.
- `PearlProofEncoding`: bounded base64/gzip/zlib decoding and canonicalization of legacy omitted-None versus current dense-None encodings.
- `miningcore-pearl-worker`: isolated current-V3 verification/certification using the pinned official Rust implementation. It verifies the generated certificate before returning it.
- `PearlProofVerifier`: a versioned binary process contract, fixed global capacity, response bounds, deadlines, process-tree termination/reaping, and independent managed target/candidate checks.
- `PearlNetworkRules`: mainnet, testnet, testnet2 and regtest fork boundaries, checked against generated official Go fixtures; mining fails closed outside current V3 rules.
- `PearlWalletRequest`: Oyster account/fee/minimum-confirmation argument ordering, network-aware addresses, canonical duplicates and grain-exact amounts, including the daemon float64 conversion boundary.
- `PearlAddress` and `PearlBlockJob`: network-aware Taproot scripts and immutable per-worker coinbase/header/transaction snapshots, witness commitments, certificate-first blocks, and verified-result/header binding.

Historical certificate **serialization** is covered; historical mining/proving is not implemented. The current mining builder and worker reject unsupported certificate versions. They are not registered as a production pool family.

## Consensus details confirmed by executable tests

Pearl's wire header is 76 bytes without a proof commitment and 108 bytes with it. Rust's in-memory previous-block and Merkle hash fields use the opposite order from wire serialization. The worker therefore calls the official `IncompleteBlockHeader::from_bytes`; it does not overlay a managed struct on those bytes.

The completed block ID is SHA256d(header108), not the jackpot hash. The proof commitment is SHA256d(version LE32 || public data). A block begins with its certificate, then header, transaction count and witness-aware transactions.

The live GBT returned a **raw 32-byte** `default_witness_commitment`, not a Bitcoin-style output script. The builder recomputes it and constructs the `6a24aa21a9ed` output prefix itself. An explicit JSON-RPC version is required by this node: omitting it produced an empty response and a daemon marshal error.

For current dense work, let L = k - k % rank and T = sampled rows × sampled columns. The share bound is assigned_target × T × (L / rank) × 128. Jackpot comparison is inclusive and interprets the jackpot as little-endian uint256. Pool bounds must not overflow; consensus block checks saturate. Exact boundaries are tested against real proofs. This is not yet a definition/implementation of Miningcore financial share credit or VarDiff.

## Native boundary and current limits

The worker supports Linux x64 development builds only. Ubuntu 26.04.1/glibc was exercised; Ubuntu 22.04, musl, ARM64, Windows and macOS packages are not certified.

The binary IPC request is `PRLJOB01`, operation byte (0 verify / 1 prove), certificate byte (3), two zero reserved bytes, header76, big-endian target32, LE32 proof length, and proof bytes. EOF is required. The successful response is `PRLRES01`, status 0, candidate byte, two zero reserved bytes, public data164, LE32 certificate length, and certificate bytes. Rejection uses a fixed 12-byte frame and nonzero exit. No input-derived diagnostic text crosses this boundary.

- Decoded plain proof: 8 MiB maximum; dense format only.
- Certificate: 60,000 bytes maximum; public data: 164 bytes.
- Two verification processes and one proving process globally; no pending worker queue.
- Managed verification deadline: 5 seconds; proving deadline: 180 seconds.
- Per-process address-space ceiling: 8 GiB; CPU ceiling: 600 seconds; four Rayon threads.
- Each process handles one request. Circuit caches cannot accumulate across requests.
- Parent cancellation kills and reaps the process tree before releasing capacity.

These are measured development limits, not production sizing recommendations. In particular, this does **not** bound a future public Stratum listener's aggregate memory or authenticate clients. The existing generic 32 KiB Stratum limit remains unchanged. A secure Pearl transport needs frame-aware pipe backpressure, authentication limits, aggregate in-flight byte budgets, connection/worker rate limits and slow-client deadlines before the family can be enabled.

## Building and testing

The worker is opt-in and is not bundled into normal Miningcore releases yet:

```bash
bash scripts/pearl/build-worker.sh
python3 scripts/pearl/test-worker.py build/pearl-native/output/miningcore-pearl-worker
# Optional slower certificate-generation contract:
python3 scripts/pearl/test-worker.py build/pearl-native/output/miningcore-pearl-worker --prove
```

The build requires Rust 1.93.1 and pins rustup toolchain selection. It checks the upstream commit and tracked cleanliness, generates both required circuit caches, uses Cargo locks, checks the worker version and records binary/cache checksums and toolchain versions. The upstream licence inventory is copied with the output. Complete release packaging and third-party dependency notices remain required.

Focused managed tests:

```bash
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -c Release \
  -p:MiningcoreStratumListenerSmoke=true --filter FullyQualifiedName~Blockchain.Pearl
```

The smoke property deliberately omits existing unrelated native libraries and cannot publish a runnable pool. Use the documented lab's full native build/regression runner for release validation.

`PearlLiveLabTests` is explicitly skipped unless `MININGCORE_PEARL_LIVE_LAB=1`. It additionally requires `MININGCORE_PEARL_LAB_ROOT`, a proof-enabled v1.5.5 regtest node on loopback 19501, Oyster on 19502, the lab-only RPC credentials in the test, and the checksum-pinned SRBMiner archive installed under `/opt/miningcore-test-lab/miners/srbminer-multi-3.7.3`. This fixture uses a local protocol harness; it does not exercise a production Miningcore pool.

The Pearl node/wallet must be built with `xmss,zkpow`, after the Rust static library/circuit caches and XMSS library. A stub verifier, simnet proof bypass or `BFNoPoWCheck` cannot certify block support.

## Recorded live evidence — 1 November 2026

Host: documented WSL Ubuntu lab; Ryzen 9 5950X, RTX 3080 Ti (12 GiB), 15 GiB system RAM. Rust/Cargo 1.93.1; source-built pearld and Oyster 1.5.5.

- SRBMiner authorize used an object with `wallet`, `agent` and `type: v2`. A notify with header, target, height, job ID and certificate version produced a raw base64 proof without compression negotiation.
- Real profile: m=32772, n=15360, k=32768, rank=128, 8 sampled rows and 16 columns; decoded legacy proof 801,488 bytes.
- Verification measured approximately 41 ms. Four-thread proving and certificate self-verification completed in 47.105 seconds. The initial 120 CPU-second budget was insufficient; it killed the process after about 61 wall seconds with two threads.
- Managed builder + real GPU miner + isolated worker + proof-enabled regtest passed. Accepted height-1 block: `7fcd0c445ce023338c9fada3eb82d8b40051c83249fa05a8540fd08105091088`.
- Coinbase: `33f81badd36f6acb13e6992a80f292f3ec46758912df8f862c9d4481f8418040`; reward 322,964,134,063 grains; certificate 59,560 bytes.
- A separately captured real easier-than-network proof verifies as a share and is correctly rejected for certification.
- Native contract suite: 25 cases without proving, including exact bound-1/bound/bound+1, malformed/truncated input, changed matrix/header, unsupported version, overflow and candidate classification.
- No mainnet block/payout or production-pool soak has been performed.

**Regtest restart caveat:** v1.5.5 `node/btcd.go::removeRegressionDB` deliberately deletes the regtest database at startup. This happened after a clean shutdown in the lab. Preserve the running node for pool/wallet restart tests, or explicitly distinguish a daemon reset from a persistent-chain restart test. This accepted block was subsequently removed by that deliberate test-network reset.

## Regression checkpoint

The documented Ubuntu native build completed, and the final warning-audited managed build reported zero warnings and errors. The full test run passed **5,704 tests**, with **24 explicitly skipped** (5,728 total), in 8 minutes 44 seconds. Skipped opt-in lanes are not counted as certification. The Pearl GPU fixture was run separately twice and passed both times. The native worker contract suite passed 25 cases after the final toolchain-pinned build.

After merging the FluentValidation update, dependency restore succeeded and the focused Pearl/collection run passed **120 tests**, with one explicitly skipped live GPU fixture. An initial restore attempt aborted with a transient internal CLR error; the successful retry used `--disable-parallel`. The full-suite count above is from the pre-merge checkpoint.

The existing lab PostgreSQL application login was insufficient for administrator-only migration fixtures. A separate administrator resolved those checks but exposed a partition fixture's assumption that its administrator is named `miningcore`. The successful run used a fresh disposable cluster on loopback 55434 with that fixture-specific setup. The existing lab application's privileges on 55432 were not elevated. The temporary cluster is stopped after testing. Logs and TRX evidence remain under `/home/ubuntu/issue-244/evidence` and `/home/ubuntu/issue-244/miningcore/build/test-lab`.

Pearl's process-wide helper capacity test deliberately fills both slots and verifies cancellation/reaping. It and the opt-in GPU fixture share a reviewed nonparallel collection so they cannot interfere with one another.

## Remaining issue #244 work

The next implementation must wire these primitives into an authenticated, bounded Pearl pool runtime and define exact difficulty/credit/VarDiff semantics. It must persist share/candidate intent before irreversible external effects, recover uncertain submission outcomes without duplicate credit, classify rewards/reorgs against exact hashes, and integrate Oyster sends with existing payout leases/reconciliation.

Still required: live historical transition evidence and production enforcement of the tested rule policy; same-tip template changes and failover; additional malformed/nonempty transaction cases; fee recipients; durable duplicate/replay handling; wallet maturity and payment recovery tests through the actual Miningcore handler; supported-platform/package lanes; complete operator example; current-mainnet compatibility evidence; independent production-like soak. Add the mineable PRL catalogue entry only after that complete path is verified.

No acceptance checkbox in #244 or supported status in #120 should be changed based only on this development harness.

## Oyster request findings and additional live evidence

A second managed GPU/prover/block-builder test accepted height 103, block `c8076b9bfdd0bcb386654ac03b81e9e1dc72278ddd3aa98144d14e96f607bad1`, coinbase `c025e0fb3b3912bf2b3836fa38aa826b5c1e88fc0b01a503db8d83ce620f9da6`. This block included a real Oyster transaction and its witness commitment, and confirmed payments of exactly 1.25 and 2.5 PRL to two disposable wallet addresses. Transaction: `b0e7b0157316f064830ad69757bd7cf20de1797ee7dd3690631f69ce322eff44`. A locked send was rejected with code -13; unlock, send and relock were exercised. The preceding 102 blocks were generated and proof-checked by the source-built node. This is wallet RPC evidence, **not** a Miningcore payout-handler or recovery test.

The request discovery exposed two incompatibilities with Bitcoin: the default BIP86 account is literally `default` (empty string is rejected), and the **required third argument is fee rate in PRL/kB**. `minimumConfirmations` is fourth, and comments are unsupported. The successful exploratory request used `1` as its third argument and therefore paid **0.198 PRL in regtest fees**, not a one-confirmation setting. The managed request builder now represents fee rate explicitly in grains/kB and emits `["default", amounts, feeRatePRLPerKb, minimumConfirmations]`. The parameter contract is pinned in [SendManyCmd](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/node/btcjson/walletsvrcmds.go#L615).

## Persistent mainnet lab node

At the user's request, a separate source-built v1.5.5 mainnet node is running in the Ubuntu lab as `miningcore-pearl-mainnet.service`. RPC listens only on `127.0.0.1:19511`, inbound P2P listening is disabled, and eight outbound peers are allowed. It has no wallet. Configuration and data are under `/home/ubuntu/issue-244/mainnet`; its randomly generated RPC credential file has mode 0600. The systemd service runs as `ubuntu`, with filesystem restrictions, no privilege escalation and a 6 GiB memory ceiling. It preserves data across restarts.

Use `sudo systemctl status miningcore-pearl-mainnet` and `sudo journalctl -u miningcore-pearl-mainnet` for status; `sudo systemctl stop miningcore-pearl-mainnet` stops it cleanly. Do not print or commit `pearld.conf`. Initial synchronization is in progress; starting this node does not certify current-mainnet template compatibility. Mainnet genesis was verified as `a18d3093b7ff618f0cdd073fa3d10374e2e0105fe7ef53ff48b55f41c58dadd7`. The reviewed node binary SHA256 is `bb2d531f16faae67216efe65c958272c3d25ed845c205501576432a1145678c9`.

The downloaded SRBMiner and WildRig archives, extracted binaries and checksum files remain under `/opt/miningcore-test-lab/miners/` for future lab runs.

## Sources and fixture provenance

- [Official header and certificate source](https://github.com/pearl-research-labs/pearl/tree/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/node/wire)
- [Official plain-proof verifier](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/zk-pow/src/api/verify.rs)
- [Official V3 seed derivation](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/zk-pow/src/api/seed.rs)
- [Oyster RPC handlers](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/wallet/rpc/jsonrpc/methods.go)
- [Pool operator's protocol notes](https://prl.suprnova.cc/stratum-spec.html), used for discovery and checked against local captures; their target prose and abbreviated binary layout are not consensus authority.

`scripts/pearl/generate-wire-vectors.go` uses the pinned Go wire implementation to regenerate the synthetic serialization vectors. Its proof bytes are deliberately invalid; those fixtures test framing only. The gzip fixtures are locally generated GPU proofs with metadata, source/miner pins and raw SHA256 digests alongside them. `scripts/pearl/verify_capture.rs` is a research harness for independent verification inside the pinned upstream crate.
