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
30 seconds** after retirement; at exactly 30 seconds they are rejected. Each
older assignment keeps its own deadline. Returning to an earlier difficulty,
repeating an assignment, broadcasting more work or replacing/disabling VarDiff
cannot extend that deadline. A no-op cancels superseded pending work without
retiring the assignment or allocating another job ID.

| Submitted work | Required target and credit |
| --- | --- |
| Current assignment | Its issued difficulty, including the existing 0.99 tolerance |
| Retired assignment within grace | Its original issued difficulty, even if the proof also exceeds the new target |
| Retired ordinary work at/after expiry | Rejected, including a proof exceeding the new ordinary-share target |
| Valid parent or auxiliary block candidate | Existing candidate exception; credit remains the original assignment, subject to normal job lookup/age and proof validation |
| BLAKE2b header-v2 job | Existing exact immutable assigned target and credit; this separate contract does not acquire generic 30-second expiry |
| Custom/direct validator without issued metadata | Current target only; `PreviousDifficulty` cannot authorize fallback |

This policy bounds the period during which a miner can deliberately keep computing
an easier retired target. The server cannot determine when hashing began; grace
is eligibility for the retained issued work, not proof of its computation time.
Thirty seconds is a fixed protocol allowance, independent of a miner's requested
difficulty or VarDiff timing settings. Long-running proofs can expire; operators
should validate miners/proxies with their normal submission latency before rollout.

The deadline uses the `TimeProvider` fixed at worker initialization and its
integer counter samples, via
[GetElapsedTime](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.getelapsedtime?view=net-10.0).
UTC and `VarDiff.LastUpdate` are not acceptance inputs. Negative elapsed time is
rejected. New connections receive independent state; reinitialization rejects
previously recorded job snapshots even for candidate validation. Invalid or
nonfinite assigned difficulties are rejected before mutation. Mutable legacy
`PreviousDifficulty` is compatibility metadata and cannot authorize or inflate
credit, whether finite, absent or malformed.

## Issuance and proof identity

Canonical/merged Bitcoin, Equihash and Verus, Handshake, Nexa, Satoshicash,
Kaspa and its custom jobs, Ethereum/Cortex, Ergo, Beam, Alephium, Xelis and
Warthog issue a worker snapshot. Reissuing a template after a difficulty change
adds `-d<assignment-generation-in-hex>` to its opaque job ID. Repeating the same
template under an unchanged assignment reuses that snapshot and ID. Notification
arrays are copied. The template's duplicate-proof registry is shared across
snapshots, so submitting the same proof under an older and newer ID cannot create
two accepted shares. Progpow's unique worker-job IDs already separate issuance;
its backing job now records the assignment and shares proof deduplication across
those IDs.

CryptoNote, Conceal and Zano retain their unique worker blobs/extranonces. Their
assignment map is keyed by **template identity plus extranonce**, since a new
template can restart its extranonce counter. Registry eviction removes those
entries. Generic snapshot metadata uses weak keys; active job count and eviction
still come from each existing worker registry. Difficulty changes do not renew
job age, clean-job, authorization or reconnect eligibility.

Pending difficulty is applied before taking the worker snapshot. Canonical
Bitcoin tracks the successfully enqueued difficulty and sends an outstanding
minimum-difficulty change before its next job broadcast. This supports repeated
[BIP 310 minimum-difficulty requests](https://bips.dev/310/) without issuing
new work at a target the miner has not received. The broader immediate
configure-notification behavior remains tracked in
[#211](https://github.com/NINJAK1DD/miningcore/issues/211).

Legacy Ethereum V1 submits only a header, Alephium's IceRiver compatibility
path can submit only group identity with an incorrect job ID, and Kaspa's
IceRiver/GodMiner compatibility path can submit an incorrect numeric ID. Identical work
reissued at a different target cannot identify its original assignment on those
paths. They enforce the **most recently issued matching work**. In-flight proofs
below that new target are rejected; using a guessed old difficulty would also
admit new work below its announced target. Explicit job-ID protocols retain the
bounded grace described above.
Kaspa's numeric compatibility lookup safely handles assignment suffixes and
rejects malformed or overflowing submitted IDs without parsing exceptions.

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
issued dynamic assignment. They verify original credit through the production
PPS recorder and PostgreSQL, reject below-target current work, reject duplicate
aliases and expired proofs, replay the accepted accounting envelope and reject
an old job after reconnect. Native CryptoNight cases cover fixed increases and
original PROP/PPLNS reward weights. The documented BLAKE2b Knots tests continue
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

The TRX guard requires all 80 native/daemon/credit cases exactly once and passed,
including the fixed-increase proofs, three canonical grace proofs and six BLAKE2b
settlement cases. It rejects missing, duplicate, skipped or failed results.
Skipped live tests are not evidence of accepted proofs or durable credit. Tests
do not change host time, production wallets or existing database tables. The lab
establishes daemon, native proof and accounting behavior; physical miner firmware
and long-running mainnet operation require deployment-specific commissioning.

## Upgrade and custom integrations

Reconnect miners during rollout so each connection obtains fresh assignment
state. Miners/proxies must echo job IDs as opaque strings; IDs after difficulty
changes now contain an assignment suffix. Earlier indefinitely eligible generic
previous-difficulty work expires, and eligible retired proofs retain original
credit even when they exceed a later target. Legacy header/group-only submissions
have the conservative behavior documented above. BLAKE2b target and credit
semantics are unchanged.

Downstream family implementations must issue the worker snapshot under the
existing assignment gate and validate the matching issued work. Do not recreate
grace from public `PreviousDifficulty`, UTC or a new VarDiff context. Blob families
must preserve template identity, register after target preparation, and remove
metadata when evicting worker jobs. Duplicate sets must remain shared when a
template is reissued with a different ID. The public `Difficulty` setter now
uses the same validation and retirement boundary as `SetDifficulty`.
