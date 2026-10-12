# Issued difficulty and in-flight share grace

Difficulty belongs to the work issued to a connection. Enabling or disabling
VarDiff must not change the target or credit of a proof already being computed.
Issue [#210](https://github.com/NINJAK1DD/miningcore/issues/210) addresses the old
fallback, which depended on `VarDiff.LastUpdate` and lost its eligibility when
static, NiceHash or minimum-difficulty assignments disabled VarDiff.

## Acceptance and credit

Each connection owns a sequence of immutable difficulty assignments. Jobs record
the assignment under which they were issued. An actual assignment change retires
the previous assignment once. Its ordinary proofs remain eligible for **less than
30 seconds** after retirement by default; at exactly 30 seconds the retired
target stops authorizing ordinary work. Each
older assignment keeps its own deadline. Returning to an earlier difficulty,
repeating an assignment, broadcasting more work or replacing/disabling VarDiff
cannot extend that deadline. A no-op cancels superseded pending work without
retiring the assignment or allocating another job ID.

| Submitted work | Required target and credit |
| --- | --- |
| Current assignment | Its issued difficulty, including the existing 0.99 tolerance |
| Retired assignment within grace | Its original issued difficulty, even if the proof also exceeds the new target |
| Retired ordinary work at/after expiry | Accepted only if it satisfies the current target, credited at the current assignment |
| Valid parent or auxiliary block candidate | Existing candidate exception; credit remains the original assignment, subject to normal job lookup/age and proof validation |
| BLAKE2b header-v2 job | Existing exact immutable assigned target and credit; this separate contract does not acquire generic 30-second expiry |
| Custom/direct validator without issued metadata | Current target only; `PreviousDifficulty` cannot authorize fallback |

This policy bounds the period during which a miner can deliberately keep computing
an easier retired target. The server cannot determine when hashing began; grace
is eligibility for the retained issued work, not proof of its computation time.
Each Stratum port can set `difficultyGraceSeconds` to an integer from **0 through
300**, default **30**. Zero disables retired-target grace. This operator setting
is independent of miner requests and VarDiff timing. Increasing it prolongs the
period in which retained easier work can earn its original credit. Measure normal
submission latency before choosing a longer allowance.

The deadline uses the `TimeProvider` fixed at worker initialization and its
integer counter samples, via
[GetElapsedTime](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.getelapsedtime?view=net-10.0).
The transport captures receipt time on the network read containing the request's
terminating LF. Coalesced requests retain that read's timestamp even when an
earlier handler is queued or slow; a split request receives the timestamp of its
final read. JSON timestamps are ignored. Validation uses that server-owned receipt
counter for grace, so processing backlog cannot expire a timely complete request.
The stamp must use the assignment's provider and cannot be in the future or before
issuance. UTC and `VarDiff.LastUpdate` are not acceptance inputs. Backward counter
movement is rejected. New connections receive independent state; reinitialization rejects
previously recorded job snapshots even for candidate validation. Invalid or
nonfinite assigned difficulties are rejected before mutation. Mutable legacy
`PreviousDifficulty` is compatibility metadata and cannot authorize or inflate
credit, whether finite, absent or malformed.

## Issuance and proof identity

Canonical/merged Bitcoin, Equihash and Verus, Handshake, Nexa, Satoshicash,
Kaspa and its custom jobs, Ethereum/Cortex, Ergo, Beam, Alephium, Xelis and
Warthog record each issued assignment. Initial issuance retains the immutable
template without allocating a worker copy. Reissuing a template after a difficulty
change allocates a snapshot and an ID from the job manager's native counter:
decimal Kaspa IDs, eight-digit hexadecimal Ethereum IDs, uppercase hexadecimal
Alephium IDs and existing family formats elsewhere. Fresh templates keep their
original native IDs. The counter allocates atomically in the positive range
1 through `int.MaxValue`, then wraps to 1. This keeps long-lived direct SOLO and
retargeting pools usable; identity is scoped to bounded recent job registries,
not lifetime history. Wrap does not recycle a recent assignment within the
2,147,483,647-allocation window. Repeating the same template under an unchanged assignment reuses
that snapshot and ID. Notification arrays are copied. The template's duplicate-proof registry is shared across
snapshots, so submitting the same proof under an older and newer ID cannot create
two accepted shares. Rejected proofs never enter the accepted-proof registry:
proof/target/expiry validation precedes the atomic duplicate commit, and concurrent
aliases have exactly one winner. Already accepted proofs retain a cheap duplicate
precheck. Progpow's unique worker-job IDs already separate issuance;
its backing job now records the assignment and shares proof deduplication across
those IDs without consuming another wire ID for its pool-local snapshot.

Duplicate keys represent the validated proof inputs. Ergo's unused `nTime` cannot
create another identity for the same nonce bytes. Ethereum uses the parsed
64-bit nonce, shared across connections and snapshots of the same template.
Cortex additionally includes the verified 42-edge solution, so distinct valid
cycles on one nonce remain distinct proofs. Kaspa canonicalizes the effective
64-bit nonce after extranonce projection; short tails use the connection prefix
followed by a left-padded tail to form exactly 16 hex characters.

Submitted byte fields in Bitcoin/merged mining, Handshake, Satoshicash, Warthog,
Nexa, Xelis, Alephium, Beam, Progpow and Equihash/Verus require the advertised
width and only ASCII `[0-9a-fA-F]` before duplicate lookup or expensive proof work.
Unadvertised `0x` prefixes, odd tails, whitespace and non-hex nibble aliases are
rejected with the family's existing protocol error. Blob nonces also enforce
exact width so a regex end anchor cannot admit a trailing newline. Numeric
Ethereum/Kaspa paths retain their documented normalization. Hex case and supported
numeric aliases cannot create another accepted credit. The general-purpose
legacy `HexToByteArray` helper is not a miner-input validator.

Equihash identities include header time as well as nonce/solution. Verus PBaaS
identifies work using the actual daemon nonce and solution, and commits duplicates
only after verification and difficulty validation; ignored submitted extranonces
cannot distinguish the same proof.

CryptoNote, Conceal and Zano retain their unique worker blobs/extranonces. Their
assignment map is keyed by **template identity plus extranonce**, since a new
template can restart its extranonce counter. Registry eviction removes those
entries. Generic snapshot metadata uses weak keys; active job count and eviction
still come from each existing worker registry. Difficulty changes do not renew
job age, clean-job, authorization or reconnect eligibility.

Kaspa-family validators hash the nonce value directly without cloning an ordinary
share's protobuf header. Only block candidates need a private header/block. A validated block
candidate retains its own cloned block for daemon submission; concurrent shares
cannot mutate the source header or change another candidate's nonce.

Pending difficulty is applied before taking the worker snapshot. Static
authorization changes in Bitcoin, Handshake, Nexa, Progpow and Satoshicash
publish replacement work immediately after the new target, under the assignment
gate. Bitcoin, Nexa and Satoshicash suggested changes and Bitcoin
[BIP 310 minimum-difficulty requests](https://bips.dev/310/) also publish
replacement work without waiting for a template broadcast. Pre-subscription
requests obtain their work during subscription; direct SOLO retains its
authorization-aware publication boundary, and BLAKE2b retains its existing
isolated publication and failure handling.

An assignment or publication failure closes the worker's assignment session and
requests disconnect **before releasing the gate**. Buffered submissions, candidate
exceptions and later producers cannot reuse partially published work. Malformed
suggestions are rejected before mutation and remain recoverable. Canonical Bitcoin,
Nexa and Satoshicash no-op suggestions do not republish unchanged work.

Generic miner-driven authorization carrying a static difficulty hint or NiceHash
identity, suggestions and minimum-difficulty configuration
share one connection-local monotonic token bucket: a burst of eight requests,
one token per ten seconds, and terminal disconnect after eight consecutive
refusals. Refusal leaves target, jobs and daemon validation untouched. There is no
IP-wide state or background timer. Ordinary authorization and share submissions
do not spend tokens and remain usable when difficulty tokens are exhausted.
BLAKE2b retains its separate existing admission and immutable-target contract.
Bitcoin suggested difficulty parses JSON numbers and strings with invariant
floating-point syntax, without locale grouping separators. `1.5` always means
1.5 under en-GB, nl-NL and de-DE; `1,5` and `1,000` are rejected as malformed.

Legacy Ethereum V1 submits only a header, Alephium's IceRiver compatibility
path can submit only group identity with an incorrect job ID, and Kaspa's
IceRiver/GodMiner compatibility path can submit an incorrect numeric ID. Identical work
reissued at a different target cannot identify its original assignment on those
paths. Kaspa/Alephium retain their original **oldest eligible template** selection,
then select the latest issued assignment of that exact template. They do not switch
to a newer template merely because the target changed. Ethereum V1 enforces the
latest matching header. In-flight proofs
below that new target are rejected; using a guessed old difficulty would also
admit new work below its announced target. Explicit job-ID protocols retain the
bounded grace described above.
Kaspa's numeric compatibility lookup uses inclusive comparison of native source
IDs and rejects malformed or overflowing submitted IDs without parsing exceptions.

Beam same-input retargets retain the original numeric job ID and resolve that ID
to the latest assignment. Beam's
[reference OpenCL miner](https://github.com/BeamMW/beam/blob/master/pow/opencl_pow.cpp)
parses numeric IDs and ignores repeated inputs; minting a different ID for the
same input can prevent its stored solutions from being submitted. The stable-ID
path therefore enforces the latest target and credit, like a header-only protocol.
Fresh Beam inputs retain their native template ID. Beam candidate submission uses
the original daemon `BlockTemplate.JobId`.

Accounting receives the original assignment credit with the existing family
normalization/multiplier. No database schema, recovery format or relay format
changes are introduced. Candidate persistence, accepted-proof boundaries,
correlated merged projections and idempotent accounting stay in their existing
managers/recorder. Historical credit is not rewritten.

## Validation

`IssuedDifficultyTests` discovers every concrete worker context and covers static,
NiceHash and minimum transitions after dynamic assignments, exact expiry, UTC
changes, replacement, no-ops, repeated changes, invalid state and blob-counter
reuse. `IssuedJobSnapshotTests` discovers the concrete/custom job types and checks
assignment separation, wire ID snapshots and shared duplicate sets.
Its native Alephium validator case preserves the family's low-difficulty error
so rejected work still follows the existing telemetry and invalid-share path.
`MergedMiningDifficultyGraceTests` exercises the production proof validator with
deterministic hash results for ordinary, parent-only, auxiliary-only and dual
candidates. The hash injection is a validator test, not daemon consensus evidence.

The canonical Bitcoin TCP tests use real Bitcoin Core templates and SHA256d
non-block proofs. Static authorization, a NiceHash TCP client with a controlled
API lookup, and minimum configuration follow an
issued dynamic assignment. They require immediate replacement work without a
template broadcast and an ordinary current proof after retired work expires.
They verify original credit through the production
PPS recorder and PostgreSQL, reject below-target current work, reject duplicate
aliases and expired proofs, replay the accepted accounting envelope and reject
an old job after reconnect. Native CryptoNight cases cover fixed increases and
original PROP/PPLNS reward weights. Concrete Kaspa, Astrix, Karlsen, Spectre and
Waglayla validators verify original credit with the family share multiplier,
including proofs exceeding the new target, expiry and shared duplicate rejection.
`DifficultyReviewHardeningTests` adds configurable grace, receipt-clock isolation,
counter concurrency/wrap, same-height blob identity and oldest-template/latest-
assignment compatibility. Concrete pool fault cases require terminal invalidation
before gate release. Transport cases cover coalesced and split request receipt
time across a blocked handler; Kaspa cases retry previously rejected proofs and
race duplicate aliases.
Their final hashes are controlled accounting vectors, not consensus evidence.
Proof-encoding cases exercise real outer validators and shared duplicate registries
across the remaining families. Cortex tests control verification outcomes for two
distinct cycles and exercise the native verifier's rejection/retry boundary;
they do not claim two captured consensus-valid Cortex cycles on one graph.
The genuine canonical Bitcoin TCP/accounting and captured Beam GPU proofs also
reject malformed aliases without additional accepted credit.
The documented BLAKE2b Knots tests continue
to exercise immutable targets and SOLO/PPS/PROP/PPLNS settlement.

Use the documented persistent Ubuntu lab on the development machine, or CI's
pinned daemon dependencies. Enable Bitcoin,
Litecoin, Dogecoin, the reviewed Knots BLAKE2b daemon and PostgreSQL as described
in [the monotonic VarDiff lab guide](vardiff-monotonic.md#regression-and-live-validation). Use a
disposable database and dedicated fixture administrator; do not grant migration
privileges to a production runtime role. Build the complete source/native suite:

```sh
dotnet restore src
mkdir -p build/test-lab
touch build/test-lab/build.log
bash scripts/release/run-warning-audited-dotnet.sh build/test-lab/build.log build src --no-restore
export LD_LIBRARY_PATH="$PWD/src/Miningcore/bin/Debug/net10.0:$PWD/src/Miningcore.Tests/bin/Debug/net10.0"
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --logger 'trx;LogFileName=difficulty-grace.trx' --results-directory build/test-lab
python3 scripts/release/test-native-family-evidence.py --self-test build/test-lab/difficulty-grace.trx
```

The TRX guard requires all 90 native/daemon/credit cases exactly once and passed,
including the fixed-increase proofs, three canonical grace proofs and six BLAKE2b
settlement cases and ten concrete Kaspa-family credit vectors. It rejects
missing, duplicate, skipped or failed results.
Skipped live tests are not evidence of accepted proofs or durable credit. Tests
do not change host time, production wallets or existing database tables. The lab
establishes daemon, native proof and accounting behavior; physical miner firmware
and long-running mainnet operation require deployment-specific commissioning.

For physical Beam GPU commissioning, the test process host provides an explicitly
invoked three-minute probe. It sends synthetic immutable inputs, validates real
BeamHash III GPU solutions through production `BeamJob` and the native verifier,
requires three accepted ordinary proofs at each of difficulties **8, 32 and 16**,
and rejects a duplicate of each accepted proof. Stage two retargets the same input
and numeric ID; stage three issues a fresh input and ID. It does not submit to a
daemon or establish durable accounting/consensus evidence.
GPU arrivals trigger the production monotonic `VarDiffManager` calculation, with
bounded lab maximums of 32 then 16, followed by the pending-assignment application.
The saved JSON identifies that retarget path. The nine captured ordinary proof
vectors are also replayed against the native verifier in `BeamGpuProofTests`.

```sh
export LD_LIBRARY_PATH="$PWD/src/Miningcore/bin/Debug/net10.0"
dotnet src/Miningcore.Tests.ProcessHost/bin/Debug/net10.0/Miningcore.Tests.ProcessHost.dll \
  beam-gpu 19810 build/test-lab/beam-gpu.json
```

Connect a verified BeamHash III miner to `127.0.0.1:19810` with a disposable lab
username, one GPU, stock clocks, API disabled and a bounded process lifetime.
When Windows-to-WSL localhost forwarding is unavailable, the probe also accepts
`<port> <evidence-file> <WSL-private-172.x-address> <Windows-private-gateway>`.
Only loopback or private `172.16.0.0/12` addresses are allowed, and the single
accepted peer must exactly match the configured gateway. Do not bind a wildcard
or forward the probe onto the LAN. The documented verified lolMiner 1.98a kit on
an RTX 3080 Ti passed all nine native proofs and duplicate checks on 2026-10-11.
IceRiver/Bitmain physical ASIC compatibility remains a separate commissioning
requirement; synthetic numeric-ID tests do not establish firmware behavior.

## Upgrade and custom integrations

Reconnect miners during rollout so each connection obtains fresh assignment
state. Miners/proxies must preserve their family's native job IDs. Earlier
indefinitely eligible generic previous-difficulty work expires; retained work can
still meet the current target at current credit, and eligible retired proofs retain original
credit even when they exceed a later target. Legacy header/group-only submissions
have the conservative behavior documented above. BLAKE2b target and credit
semantics are unchanged.

Downstream family implementations must issue the worker snapshot under the
existing assignment gate and validate the matching issued work. Do not recreate
grace from public `PreviousDifficulty`, UTC or a new VarDiff context. Blob families
must preserve template identity, register after target preparation, and remove
metadata when evicting worker jobs. Require the recorded template object to still
be current; matching height alone cannot identify a same-height reorganization.
Duplicate sets must remain shared when a template is reissued with a different ID.
Custom snapshot callers must supply the manager's native ID allocator; the optional
fallback suffix is only for direct/custom callers without a wire-format allocator.
The public `Difficulty` setter now
uses the same validation and retirement boundary as `SetDifficulty`.
