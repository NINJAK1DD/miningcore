# Bitcoin BLAKE2b header-v2 mining

This is support for a **separate hard-fork chain**, not a new algorithm selectable on
SHA-256d Bitcoin. Use `coin: "bitcoin-blake2b"`; the existing `bitcoin` template and its
direct-coinbase SOLO policy are unchanged. Miningcore labels this chain `BTCB2B` to keep
pool/accounting identities distinct; that label is not a claim about an exchange ticker.

[BIP-110](https://github.com/bitcoin/bips/blob/master/bip-0110.mediawiki) describes the
Reduced Data Temporary Softfork. It is not the BLAKE2b proof-of-work specification. The
later hard fork is implemented by the pinned Knots sources listed below.

## Compatibility boundary

- A Miningcore build containing this feature is required; v0.3.0 does not contain it.
- The reviewed node is **Bitcoin Knots v29.4.1.knots20260508**, commit
  `8c85b1585dac23f964e2dd32045624de7f02aa58`. Startup requires its version and Knots identifier,
  an active deployment with the expected activation height, and mandatory GBT rule `!blake2b`.
  Version strings are compatibility checks, not proof of binary authenticity: independently
  verify the upstream release checksums and signatures.
  Runtime work re-attests version, chain and deployment on the first successful template
  poll after a 30-second cache expires, and before new work after a GBT/activation-parent RPC outage.
  Failed attestation RPCs withhold fresh work and retry with bounded exponential backoff
  (1–30 seconds), restarting all identity checks after the delay. Only a complete successful
  attestation resets the backoff. Successful identity or deployment mismatches
  fault only the affected BLAKE2b pool and close its mining admission. Cached attestation bounds
  detection latency; it does not authenticate binaries or eliminate an endpoint replacement
  between RPC calls. Stop Miningcore before changing its daemon binary or chain configuration.
  There is no operator version-pin bypass. Urgent security-update compatibility needs a
  reviewed source/build update, not merely an acknowledged version string; the safe upgrade
  policy is tracked in [#151](https://github.com/NINJAK1DD/miningcore/issues/151).
  Attestation shares the serialized polling loop, not a background timer: a cache-expiring
  update can pay three additional sequential RPCs. Ordinary template polling refreshes the
  cache even without a new block. This deliberate latency/safety trade-off avoids concurrent
  attestation-state races; it does not promise zero added new-tip latency.
- Mainnet first uses header-v2 at height **961640**. Its activation coinbase headline is
  `8-30 NYPost Deride And Conquer` and the one-time target shift is 22. Miningcore validates
  that first target against the parent and the mainnet proof-of-work limit; it does not
  apply another shift to ordinary shares or subsequent daemon targets.
  The parent-only comparison is restricted to mainnet carry-forward heights. At a retarget
  boundary or on min-difficulty regtest, the daemon owns difficulty selection; Miningcore
  still validates GBT target/bits and rejects malformed parent metadata. See the pinned
  [Knots difficulty selection](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/pow.cpp#L32-L89).
- Only mainnet and isolated regtest are configured. Testnet4 and signet are not advertised.
  The regtest fixture uses activation 20 and shift 20; its headline is
  `Miningcore BLAKE2b regtest`. These are a test contract, not mainnet settings.
  Regtest activation height/headline may be explicitly matched to a custom test node;
  its target shift remains 20 in the pinned daemon's `consensus/params.h` default.
  Height/headline overrides do not provide a target-shift override.
- The node release is stable. The project's compatible DATUM gateway/miner ecosystem is
  still described as public beta. A stable node does not prove compatibility with every
  ASIC, firmware, proxy, rental service or public network deployment.

## Node and wallet isolation

Use a separate daemon data directory, wallet, RPC credentials, pool ID and ledger attribution.
Do not repoint your SHA-256d Bitcoin pool or use a copied production wallet as a shortcut.
The chains share historical addresses and transactions; assess replay and wallet risks
independently before funding reserves or making payments. Miningcore does not add replay
protection to transactions created by a daemon wallet.

Run the verified node in that dedicated directory. For a same-host deployment alongside
ordinary Bitcoin, the example deliberately uses non-default ports. Confirm these are unused
by every local daemon, testnet and service first:

```ini
server=1
rpcbind=127.0.0.1
rpcallowip=127.0.0.1
rpcport=18332
rpcuser=CHANGE_ME_BLAKE2B_RPC_USER
rpcpassword=CHANGE_ME_BLAKE2B_RPC_PASSWORD
port=18333
bind=0.0.0.0:18333
bind=127.0.0.1:18334=onion
```

This remains **mainnet**: do not add `testnet`, `testnet4`, `signet` or `regtest` to a
mainnet deployment. Use the dedicated `-datadir` with every `bitcoin-cli` command too.
Let the daemon synchronize, load its dedicated payout wallet and inspect:

```console
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getnetworkinfo
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getdeploymentinfo
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getblocktemplate '{"rules":["segwit","blake2b"]}'
```

`getdeploymentinfo.blake2b` must have the expected height and `active: true`; the template
must advertise `!blake2b`. Stable Knots does **not** promise a `coinbaseaux.blake2b_headline`
field. Miningcore uses reviewed typed activation metadata, not an assumed optional GBT field.
The pool wallet address must belong to the loaded wallet if the pool is to pay rewards.
Use the daemon endpoint's `httpPath` to select a wallet when more than one is loaded.

## Configure Miningcore

Start from [bitcoin_blake2b_pool.json](../examples/bitcoin_blake2b_pool.json). Keep this
configuration outside the checkout, replace every `CHANGE_ME` value, and follow the
[operator preflight](operations.md#before-accepting-miners). Review the RPC ports, pool-wallet
and fee addresses, PostgreSQL credentials, logging and recovery paths before opening Stratum.

The sample uses **custodial SOLO**: the chain wallet receives the block reward, then
Miningcore pays the winning miner after maturity, minus reviewed fees. It does not use
canonical Bitcoin's default direct-coinbase SOLO mode. Do not add `soloCoinbasePayout` or
`bip54Coinbase`, even as false: those settings belong to a different reviewed runtime.

SOLO, PROP and PPLNS use their existing payout/accounting paths. PPS uses immutable assigned
difficulty evidence and the existing transactional credit ledger; follow the complete
[PPS reserve, schema and recovery checklist](pps.md) before selecting it. Keep both pool
and cluster payment processing enabled for PPS. No new database schema is introduced.
Relay receivers must understand the new family and have the same template/chain contract;
do not introduce a new chain into a mixed-version accounting deployment.

Keep the fee entry at zero until its address and intended percentage are reviewed.
Do not infer current profitability, market value or reserve adequacy from hash-rate telemetry.
Startup reserves the activation-headline scriptSig budget even if the current job does not
need the headline. With the shipped network contracts, `paymentProcessing.coinbaseString`
allows **24 UTF-8 bytes after trimming** (24 ASCII characters, fewer for multibyte text).
Oversized markers are rejected before daemon access. Pinned Knots emits an empty
`coinbaseaux` object; nonempty `coinbaseaux.flags` are refused explicitly before serialization,
not silently incorporated into the startup budget or reported only as a later length overflow.

## Miner protocol and difficulty

Connect compatible BLAKE2b/Sia-style miners directly to Miningcore, using a valid address
on this chain as the username (`ADDRESS.worker`). A DATUM gateway is not required between
the miner and Miningcore; the separate DATUM pooled-mining protocol is not implemented.
SHA-256d hardware, including SHA-256 Bitaxe devices, cannot mine this chain.

The [upstream miner guide](https://btc-blake2b.org/miners) lists Antminer A3 and Sia-style
Goldshell devices. That is upstream compatibility information, **not** a Miningcore firmware
certification. No physical BLAKE2b ASIC/firmware is claimed tested by this implementation.
Commission each miner/proxy on an isolated endpoint before sending production hash power.

Physical GPU validation used an RTX 3080 Ti with the unmodified OpenCL kernel from
[PyBLOCK miner revision 618ec513](https://github.com/GaltRanch/pyblock-miner/tree/618ec5130feca063ecd4d0ae634633d3d3ebc644)
and a separate, bounded Stratum adapter that reads Miningcore's exact compact share target.
Across the four payout schemes, the full Miningcore process recorded 98 accepted GPU-generated
shares and 98 distinct daemon-accepted blocks in an isolated PostgreSQL/regtest deployment.
Every accepted share also met the deliberately easy regtest network target; these are separate
share and block counts, not an estimate of mainnet block-finding performance.
This included PPS credits, duplicate/malformed rejection, reconnects, graceful restarts,
changing VarDiff targets, and daemon outage/recovery. GPU results were independently
verified with Python's BLAKE2b before submission, then checked against the daemon's active chain.
This validates the GPU kernel and pool path, **not** PyBLOCK's complete Rust client, ASIC
firmware, production payout economics, or a sustained mainnet soak. In particular, do not
assume a miner that rounds numerical difficulty can safely ignore the compact target in notify.

The production wire contract is Sia-style **profile 0**, with hasher time rolling disabled:

- Subscribe returns a four-byte connection extranonce and an eight-byte extranonce2 size.
- Notify contains the hidden previous hash, a 39-byte commitment in `coinb1`, empty `coinb2`,
  an empty merkle list, an eight-digit compact **share target**, and 16-digit miner time.
- Submit requires exactly five JSON strings: worker, job ID, extranonce2, time and nonce.
  Extranonce2, time and nonce must each contain exactly 16 hexadecimal characters.
  Extra version bits and shortened legacy fields are rejected, not padded or coerced.
- Version rolling is disabled; miners cannot change consensus-owned header fields. The
  miner-time bytes are nonce space for this fixed-time profile, not permission to change
  the committed consensus timestamp.
- Difficulty uses Bitcoin's `0x1d00ffff` reference target and multiplier 1, as in the
  reviewed gateway accounting contract. This is distinct from a miner display's SI units.
  Each assigned target is converted with exact integer arithmetic and truncated to the
  compact value actually sent on the wire. A valid network candidate is never discarded
  solely because its assigned share target is harder than the network target.
- Each job keeps its assigned difficulty snapshot across VarDiff changes. Changed targets
  require fresh notify data as well as `mining.set_difficulty`.
  Successful BIP310 `minimum-difficulty` changes announce difficulty before the new job too.
- Mainnet endpoint difficulty and VarDiff minimum/maximum must be at least **1**. This is
  a conservative miner-compatibility floor from the reviewed CONVOY high-32-bit admission
  boundary, not a Knots consensus rule. Sub-floor targets are allowed only on isolated
  regtest for software proof generation. Wider physical hardware ranges need commissioning
  before this production boundary can be relaxed; firmware doing harder work than the
  assigned target can otherwise be under-credited, particularly under PPS.
- Connection suffixes never wrap within a running allocator. A random 128-bit coinbase
  discriminator separates job commitments across processes and restarts. Duplicate work
  remains duplicate even if hexadecimal casing or the assigned target changes.

All four ASIC layouts and nonzero XOR-mask variants are covered by official Knots vector
tests (`HeaderV2_MatchesStableKnotsVectors`), but this
does not advertise selectable wire profiles 1–3 or anti-withholding service. Production
uses a zero XOR key. There is no user-supplied header-flags or profile override.

### Software commissioning adapter contract

The private lab adapter is not a supported public miner distribution. An operator building
an adapter around the PyBLOCK kernel can use the wire contract above and the public
[regtest wire fixture](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bWireSession.cs)
and [independent proof reconstruction](../src/Miningcore.Tests/Blockchain/BitcoinBlake2b/BitcoinBlake2bRegtestTests.cs)
as executable references:

- Preserve the connection extranonce, exact issued job ID and compact target for each job;
  do not substitute a target rounded from the numeric difficulty announcement.
- For profile 0, construct the 52-byte first-stage input as `0x00 || coinb1 || extranonce1 || extranonce2`.
  Hash it with BLAKE2b-256, then hash `hidden_previous || nonce || miner_time || first_stage_digest`
  with BLAKE2b-256. All concatenations are decoded bytes, not ASCII hex.
- Compare the final digest as a big-endian integer against the decoded compact target.
  Preserve the eight-byte nonce/time fields and submit the exact five-string wire request.
- Honor `clean_jobs`, target changes and reconnects; stop submitting invalidated work and
  bound pending jobs, GPU batches and outstanding requests. Repeated miner-requested
  difficulty changes are not a commissioning stress-test substitute; a dedicated request
  budget is tracked in [#152](https://github.com/NINJAK1DD/miningcore/issues/152).

First validate against the pinned isolated regtest node. These references do not certify
third-party miner firmware or provide a production-ready adapter.

## Troubleshooting and validation limits

- **Startup refuses a node:** check exact version, RPC authentication, selected chain,
  deployment state, and `!blake2b`. Do not remove the gate or substitute the `bitcoin` template.
- **Activation parent RPC is temporarily unavailable:** work verification retries with
  exponential backoff from 1 to 30 seconds. No unverified job is published; previously
  verified work is retained. Forced rebroadcasts emit nothing until that first verified job
  exists, so startup remains blocked before listener activation. Transport failures do not
  stop unrelated pools. A successful
  but malformed/contradictory consensus response still invokes the terminal failure path.
- **Malformed or unknown work:** check firmware/proxy field lengths and job preservation.
  A successful but incompatible daemon identity, chain or deployment response faults the
  **affected BLAKE2b pool**, closes its listeners and rejects new submissions. Other pools
  remain running. This is distinct from retryable transport errors. Stop Miningcore before
  replacing or upgrading a daemon; review compatibility before restarting.
  Ordinary Bitcoin Stratum translation is not a compatible substitute.
- **Unexpected low-difficulty shares:** verify the notify compact target and miner protocol,
  not only the displayed `set_difficulty`. Avoid unreviewed time/version rolling.
  A stock SHA-256d Sv1 miner, or a generic BLAKE2b miner without this Sia-style header-v2
  contract, may show 100% low-difficulty rejects or malformed-work errors. Use a compatible
  miner; changing its displayed algorithm name or difficulty does not translate the protocol.
- **Daemon rejects a candidate:** preserve the submission hash, daemon rejection reason,
  template and recovery evidence. Independently look up the block. A missing response is
  not acceptance, and `duplicate-invalid` must never be treated as success.
- **Accounting pipeline stops:** follow [recovery guidance](troubleshooting.md); never import
  a quarantine file as a recovery journal. Preserve PostgreSQL and all journals first.

### Multi-pool failure isolation

BLAKE2b can share a Miningcore process with other enabled pools. A terminal failure in its
job pipeline or pool lifetime closes that pool's admission gate and listeners without
terminating healthy sibling pools. Its public pool API reports `miningState` as `starting`,
`online`, `draining`, `faulted` or `stopping`. `draining` means local admission is closed
after a fault but previously owned operations remain; `faulted` means that drain has finished.
`stopping` takes precedence once host shutdown is requested. The separate `miningFaulted`
Boolean stays true after a local fault, including during host shutdown, so operators can
still identify a faulted pool while restarting. It is false before a local fault; ordinary
non-isolated pool responses omit both fields. A fault also produces an operator notification
and an error log. The first three secondary failures are logged at Info, then further failures
at Debug without repeating notifications; expected host-shutdown noise remains suppressed.
Outstanding drains warn after the first 30 seconds and every 30 seconds thereafter until
completion. Counts are outstanding admission leases, including nested payout-cycle and
classification/commit leases, not distinct shares, payments or RPCs. Fast drains emit only
the completion message.
The state describes local mining availability, not proof of wallet or database health.

New payout cycles and wallet operations are skipped for the isolated pool. Block classification
rechecks admission after database loading and again before committing daemon observations.
If isolation occurs during classification, **all results are discarded** without changing
persisted block/reward/balance state. A commit lease acquired before isolation instead allows
those owned database transitions and their post-commit notifications to finish; it never
authorizes a later wallet payment, which has its own admission check. Submissions and
wallet operations already owned before isolation are allowed to finish: cancelling an RPC
after a daemon accepted a block or payment can lose its financial outcome. A slow owned
submission remains tracked even after the local connection-drain timeout; disconnected miners
must not interpret a missing acknowledgement as proof that their share was not recorded.
The submission lease spans candidate persistence and `PersistenceAdmission`: for ordinary
shares, that is recorder queue admission (or an emergency journal force-flush), **not** the
normal queued PostgreSQL commit. Ordinary-share durability continues to rely on the shared
Share Recorder's flush/failover policy; local isolation neither strengthens nor weakens it.
Existing accepted relay shares, recorder entries, PPS liabilities and recovery evidence are
not discarded, and their accounting identities are not reset.

Listener teardown closes miner sockets promptly; it does not wait for an error response to
reach an unresponsive client. Miners may therefore see a transport close rather than a JSON-RPC
error. Outstanding submissions retain ownership independently of that socket. Reconnects in
the teardown window are refused without per-attempt error stack traces.

There is no automatic restart or compatibility bypass. Correct the underlying daemon or
configuration problem, inspect outstanding block/payment outcomes and restart Miningcore in
a planned maintenance window. The failed pool stays isolated (`draining` then `faulted`)
until that restart.
Separate services remain an option when independent operator restarts are required.

This is **not** isolation from shared infrastructure failure. Invalid cluster configuration,
shared startup preflight failures, unrecoverable database/journal failures, uncertain wallet
outcomes and process-wide resource failures retain their existing fail-closed shutdown policy.
Other pools cannot safely continue accepting financial work when shared durability is lost.

Explorer metadata remains omitted until a chain-specific service and its block, transaction
and address routes are verified. Do not substitute a SHA-256d Bitcoin explorer for this fork.

Automated tests include the five official header-v2 vectors, strict configuration and share
parsing, exact target boundaries, difficulty snapshots, and a pinned-node activation/submission
fixture. The node fixture is enabled by `MININGCORE_TEST_BLAKE2B_BITCOIND` and is explicitly
skipped if that binary is unavailable; the main CI lane installs its checksum-pinned binary.
It drives real manager startup and newline-delimited TCP subscribe/authorize/configure/submit,
constructs Sia-style proofs independently from notify data, and verifies accepted blocks for
SOLO/PPS/PROP/PPLNS. It also checks VarDiff notification ordering, strict JSON rejection,
PPS admission evidence, coinbase maturity, fee allocation, and a confirmed wallet payment.
Its persistence sink is substituted: this test is not a PostgreSQL ledger integration test.
A second, explicitly gated test requires both that binary and `MININGCORE_TEST_POSTGRES`.
It feeds the real miner's accepted proof into the PostgreSQL accounting repository, verifies
PPS credit/remainder precision, duplicate replay and conflicting-payload rejection, and runs
SOLO/PROP/PPLNS allocation against actual share/balance tables. Confirmed or orphaned PPS
blocks cannot credit the same liability again or reverse it. Each run owns a disposable schema.
Inspect actual test results before deployment—test code existing is not evidence that a run passed.
Real-network maturity, payout liquidity, firmware behavior and long-running VarDiff require
operator commissioning beyond isolated regtest.

## Immutable source provenance

Protocol baseline rechecked against upstream on 2026-09-04 and 2026-09-05:

Loader constants enforce this reviewed compatibility boundary; they are not independent
proof of upstream consensus. That evidence is the pinned source audit, official vectors
and accepted-block integration tests. Changes to these constants require renewed review.

| Contract | Reviewed source |
| --- | --- |
| Stable node and release | [Knots v29.4.1.knots20260508](https://github.com/bitcoinknots/bitcoin/tree/8c85b1585dac23f964e2dd32045624de7f02aa58) |
| Header layout | [src/primitives/block.h](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/primitives/block.h) |
| H1/H2, ASIC profiles, PoW, XOR | [src/primitives/block.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/primitives/block.cpp) |
| Official vectors | [block_header_v2.json](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/test/data/block_header_v2.json) |
| GBT rules and version | [src/rpc/mining.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/rpc/mining.cpp) |
| Activation parameters and target shift | [chainparams.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/kernel/chainparams.cpp), [pow.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/pow.cpp) |
| Miner work and target accounting | [CONVOY datum_pow.c](https://github.com/CONVOYMining/datum_gateway/blob/b9ea7dc3eb91352565ab487ec55ed6ee5964a440/src/datum_pow.c) |
| Miner notify, submit and payout coinbase selection | [CONVOY datum_stratum.c](https://github.com/CONVOYMining/datum_gateway/blob/b9ea7dc3eb91352565ab487ec55ed6ee5964a440/src/datum_stratum.c) |

The Knots and CONVOY default heads were unchanged from these pins at the recheck. New,
unmerged gateway proposals addressed strict parsing, duplicate replies, diagnostics and C
memory safety; they do not redefine this consensus baseline. Re-audit upstream before merge
and before accepting a new daemon revision rather than automatically tracking a moving branch.
