# Xelis difficulty target correction (#158)

## Reviewed upstream contract

The reviewed daemon **and bundled official miner** are Xelis v1.25.0, commit
[`db59b5c246ab0e40386d6c241dcd77f0aea84b10`](https://github.com/xelis-project/xelis-blockchain/tree/db59b5c246ab0e40386d6c241dcd77f0aea84b10).
The following source files define the contract:

- [`xelis_common/src/difficulty.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/difficulty.rs):
  `floor(U256::MAX / difficulty)`; a zero divisor is invalid. Hashes are unsigned
  big-endian integers, and equality with the target is accepted (`hash <= target`).
- [`xelis_common/src/varuint.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/varuint.rs):
  difficulty is an unsigned 256-bit integer, serialized in JSON as a decimal string.
- [`xelis_miner/src/main.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_miner/src/main.rs):
  the maintained miner calls those same target-construction and comparison functions.
- [`xelis_common/src/crypto/hash.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/crypto/hash.rs):
  v1 copies work into a private, zero-filled 200-byte aligned state before hashing.
- [`xelis_daemon/src/rpc/rpc.rs`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_daemon/src/rpc/rpc.rs):
  `get_block_template` derives its algorithm from the header version and difficulty
  from its tips; `get_miner_work` decodes that header and derives the same fields
  from its version/tips. Algorithm and difficulty must therefore agree.

Miningcore now preserves RPC difficulty as `BigInteger` and divides before
serializing the positive target to exactly 32 bytes, in big-endian order with
leading zero padding. Modern decimal strings and legacy JSON integer tokens
are accepted. Missing miner-work difficulty, zero, negative, fractional,
floating-point/exponent, nonnumeric, and values above `2^256-1` fail deliberately.
Decimal strings are limited to 78 ASCII digits before parsing. Work RPC calls
also reject unquoted numeric tokens longer than 78 characters **before** Json.NET
can construct a large integer. Decompressed response bodies are limited to 8 MiB
for block headers and 4 KiB for fixed-size miner work, including chunked bodies;
body reads retain the RPC timeout and cancellation. Other RPC methods retain
their existing response policies. The unused floating-point target overload and
duplicate `CheckDiff` helpers are removed; production uses the full-width
`uint256 <=` candidate comparison.

Both RPC difficulty fields are required; both algorithm and difficulty must agree. Canonical `xel/v1` and
`xel/v2` are explicitly allowed; missing, unknown and v3 algorithms are rejected.
Unsupported work never silently falls back to a different hasher. Failed header
requests stop before requesting miner work, and either failed/null response
produces a redacted Error-level `XelisJobManager.UpdateJob` diagnostic, including
the safe failure category and numeric RPC code without remote error text. Caller
cancellation is silent. Repeated failures in one outage are counted without
repeating Error logs for transient outages; suspended refresh failures emit a
reminder at most once every ten minutes of monotonic elapsed time, independent
of system wall-clock corrections. Successful recovery reports `stage: Recovered` at Info with
the consecutive failure count. A newly confirmed incompatibility always emits
its own rejection diagnostic, even during an existing outage.

Confirmed incompatibility retires a shared work epoch covering **every** recent
job and worker snapshot, clears the manager's current job and rejects new use of
retired jobs before hashing. Proofs whose verification overlaps retirement are
also rejected before returning a share. The suspension transition disconnects
all existing sessions **without an IP ban**, allowing configured miner failover.
While suspended, the listener closes newly accepted TCP sockets before creating
a Stratum connection, acquiring admission capacity, or doing TLS/Stratum work.
Registration and subscribe/authorize guards close concurrent transition windows
before address RPCs. The listener remains bound and admits sessions again when
supported work recovers. Miners without a failover pool can repeatedly reconnect
and be refused throughout a long suspension; this expected churn is not by
itself evidence of an attack. Late
retired-job submissions carry a dedicated rejection marker and do not increment
invalid-share counts or trigger bans, including the tail after supported recovery.
Unknown jobs and ordinary invalid proofs retain their normal rejection policies.
Broadcasting/VarDiff cannot
reissue retired work. A subsequent fully validated, supported response opens a
fresh epoch. Old queues may retain references for bounded cleanup, but those jobs
remain retired permanently, including after recovery.

The **first** confirmed unsupported algorithm or header/work consensus mismatch
retires work and initiates failover immediately. There is deliberately no
second-response delay: the pinned daemon derives both consensus fields from the
same header, so an inconsistent response cannot establish usable mining work.
Repeated incompatibilities in that epoch do not repeat the disconnect sweep.
An authorization overlapping retirement closes quietly with normal assignment
cleanup and gate release; this expected transition produces no assignment Error.

Transient HTTP/RPC outages and caller cancellation do not retire compatible
assignments or reopen an already suspended epoch. Refresh requests are serialized
to prevent older responses from racing past a newer incompatibility decision.
Operators should monitor daemon health and time since the last successful work
refresh during a prolonged RPC outage; preserving compatible cached work does
not establish that daemon work is fresh. Count a successfully validated unchanged
template as a successful acquisition; age since the last new job ID or block
height is not a substitute for RPC freshness monitoring.
Block RPCs already admitted before retirement can complete and retain an accepted
network receipt; retirement does not erase an already submitted block outcome.
Likewise, shares admitted at the final epoch check may finish publication and
accounting after retirement. This check is the admission boundary, not a global
transaction with later Share Recorder processing. A deterministic test holds an
admitted block RPC open through retirement, preserves acceptance, and rejects an
unrelated obsolete submission without hashing it.

While suspended, the null current job gives the existing candidate/VarDiff
ceiling an unbounded sentinel. This cannot admit work or increase VarDiff through
accepted shares; fresh supported work restores the target-derived ceiling.

The original generic `ExecuteAsync` CLR signature (logger, method, token, payload,
throwOnError) and public constructor remain available. Bounded responses use
`ExecuteBoundedAsync`, avoiding source-overload ambiguity and binary breakage for
previously compiled consumers. Dedicated regressions invoke the original method
signature and exercise an elapsed HTTP timeout after headers, separately from
caller-triggered cancellation. Non-2xx error bodies retain HTTP status attribution
while still enforcing the numeric bound; runs of dashes alone are not numbers.

## Operator compatibility

### Timestamp-only template freshness

The pinned daemon's
[`BlockHeader::get_work_hash`](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_common/src/block/header.rs#L211)
covers version, height, tips and transactions; the timestamp is a separate field
of the PoW challenge. Its
[template and validation code](https://github.com/xelis-project/xelis-blockchain/blob/db59b5c246ab0e40386d6c241dcd77f0aea84b10/xelis_daemon/src/core/blockchain.rs#L1429)
updates cached-template time, rejects excessively future-dated blocks, and
rejects timestamps earlier than a parent. A newer template timestamp alone does
not demonstrate that an earlier issued timestamp is invalid.

The real-daemon fixture now obtains a newer template timestamp while immutable
work remains fixed, asks the daemon for miner work after changing **only** the
original header's timestamp, and verifies matching immutable prefix, remaining
challenge bytes, algorithm and exact difficulty. Production refresh retains its
original job; a real proof from that originally issued job is then accepted by
the daemon, with its block identity checked. This closes the requested
daemon-backed eligibility test without introducing an unproven timestamp-only
replacement policy.

This evidence covers the pinned v1 stagenet scenario with unchanged chain tips.
It does not certify indefinite daemon outages, current-mainnet timestamp rules,
or work remaining useful after chain-state changes. Daemon/refresh-age monitoring
and the separate #120 production-certification work remain necessary.

### Xelis extension API changes

This correction changes these public/protected Xelis extension contracts.
Downstream extensions must update and rebuild against the new assembly:

| Previous contract | Current contract / migration |
| --- | --- |
| `XelisJob.ProcessShare` returns `Share` | Returns `(Share Share, string BlockTemplate)`; carry the candidate's own immutable template into submission |
| Protected virtual `XelisJob.ProcessShareInternal` returns `Share` | Overrides return the same tuple and retain epoch validation in the public entry point |
| `XelisUtils.DifficultyToTarget(double)` | Use the exact `BigInteger` overload with a positive integral value within U256; no floating-point target approximation |
| Protected `XelisJobManager.SubmitBlockAsync(Share, GetBlockHeaderResponse, CancellationToken)` | Virtual `SubmitBlockAsync(Share, string, CancellationToken)` takes the per-share serialized header |

These are source/binary breaking changes for separately compiled Xelis callers
and subclasses, even though repository callers are migrated. No wrapper restores
shared mutable solved headers or imprecise target conversion. The general RPC
client's original method and constructor contracts remain compatible as described
above. Rebuild custom Xelis extensions together with the application on upgrade
or rollback.

### Mining compatibility

Difficulty 1 produces `ff` repeated 32 times. Difficulty 2 produces `7f` followed
by 31 `ff` bytes. The 31/32-byte transition is between difficulties 255 and 256;
257 produces the asymmetric target `00ff` repeated 16 times. The former signed
little-endian conversion either produced zero for full-width/sign-extended
targets or reversed the significant bytes of shorter targets. The former
multiply/divide-by-255 approximation also disagreed with daemon integer rounding.
All of these are corrected, so block-candidate classification and its existing
target-derived credit ceiling can change to match the daemon. Ordinary worker
difficulty, share admission tolerance, stored-share units and payout formulas
retain their existing policies. Historical shares are not rewritten.

No configuration or database migration is required. Restart Miningcore to
replace cached jobs. Monitor accepted blocks and low-difficulty/rejected-block
rates after deploying. If rollback is necessary, stop the affected pool and
restore the previous application build; reverting also restores the defective
target construction, so investigate before resuming mining.

The live-test work also exposed a prerequisite memory-safety defect in the v1
managed wrapper: its native function mutates 200 bytes regardless of the supplied
length, while the wrapper passed 112-byte MinerWork directly. The wrapper now
copies into a private aligned 200-byte state and rejects larger inputs. This
prevents out-of-bounds access and mutation of caller work, including the work
used later for block identity. Independent upstream zero-input and text vectors
are pinned to [`xelis-hash` commit `f48262b931438fdbf2932711613e020c02f1ea04`](https://github.com/xelis-project/xelis-hash/blob/f48262b931438fdbf2932711613e020c02f1ea04/src/v1.rs).
They verify native output, padding, caller immutability and repeatability. No
v2 hashing behavior is changed. GNU v1 dispatch is preserved; MSVC x64 uses the
existing scalar stage and a platform prefetch spelling. The AES helper name is
scoped to avoid colliding with MSVC's `xtime` type.

The previous v1 hash depended on bytes beyond the work array, so historical
acceptance/rejection is not reliable evidence of valid proof by itself. This
correction does not reconstruct or reprice historical shares. The release notes
call out this impact explicitly.

Each candidate returns its own serialized header alongside its share; job inputs
are never overwritten with solved work. A deterministic two-worker test holds
both candidates before either submission captures its RPC payload, then verifies
distinct submitted headers, matching proof identities and unchanged shared input.
Polling reissues a new job/target when exact difficulty or algorithm changes even
if the work prefix and height are unchanged; network-difficulty statistics update
in that case too. Issued jobs cache their algorithm and exact difficulty.

`Share.NetworkDifficulty` still uses the existing double reporting/accounting
model and can round above `2^53`; consensus targets use exact integers. The
non-consensus `GetDifficultyResponse.Difficulty` and block lookup's
`Difficulty`/`CumulativeDifficulty` fields remain double. Their typed modernization,
v3 support, and payout certification are tracked under #120.

This is a focused child correction of [#120](https://github.com/NINJAK1DD/miningcore/issues/120).
It does **not** certify current-mainnet Xelis support: this runtime has v1/v2
hashers, while maintained Xelis also has v3 and subsequent protocol upgrades.
Algorithm modernization, payout certification, and a production soak remain
separate #120 work. The live fixture below uses isolated **stagenet height 1**
(`xel/v1`, difficulty 30,000), with real PoW validation enabled.

## Reproducible validation

A zero-valued 256-bit proof is valid against every positive target. Its proof
difficulty is treated as positive infinity for admission only, avoiding division
by zero; persisted share credit still comes from the finite issued assignment
and the job's candidate-credit ceiling. Controlled zero-digest tests cover both
hash algorithms, difficulty 1, a production target and U256's upper bound,
duplicate rejection, immutable solved headers and manager-level RPC submission.

Submission validation rejects null/non-array parameters, fewer than three fields,
and missing, empty or non-string job IDs/nonces with a bounded Stratum error.
Validation precedes job lookup, proof hashing and daemon submission. Additional
trailing fields retain their existing compatibility behavior. TCP regressions
verify the controlled error preserves the authorized session and jobs without
daemon calls; ordinary invalid-share accounting still applies.

`XelisDifficultyTests` contains independently calculated fixed vectors for
difficulties 1, 2, 3, 128, 255, 256, 257, 1024, 71,135,336,520,
`2^53-1`, `2^53+1`, and `2^256-1`. Reproduce any vector independently with Python:

```python
difficulty = 71135336520
print((((1 << 256) - 1) // difficulty).to_bytes(32, 'big').hex())
```

Tests verify full-width RPC integers/strings, malformed inputs, exact job target
initialization, inclusive candidate boundaries, low-difficulty rejection,
ordinary share credit, candidate credit caps and duplicate rejection. The
controlled-digest tests substitute only hash results, leaving initialization,
issuance and accounting on their production paths. RPC consumer fixtures cover
difficulty 1 and integers above double's consecutive-integer range.

For live validation, use the documented Ubuntu WSL test lab with a separate
Linux test copy. Obtain the official v1.25.0
[`x86_64-unknown-linux-gnu.tar.gz`](https://github.com/xelis-project/xelis-blockchain/releases/download/v1.25.0/x86_64-unknown-linux-gnu.tar.gz)
and verify its SHA-256 against the upstream release checksums before extraction:

```text
SHA-256: 424ac65de320a835b4cbe2c2a8b9140b12e8bab86cf5ebda89ffee024947135e
```

From the Linux checkout, after a normal native build:

```bash
export MININGCORE_TEST_XELISD=/absolute/path/to/v1.25.0/xelis_daemon
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~Xelis'
```

`XelisDaemonTests` creates a unique temporary data directory, binds RPC to a
dynamic loopback port, disables P2P, and uses a published test address without
wallet keys. It resolves the production serializer registration, uses `RpcClient`
and the production `UpdateJob`/`SubmitShareAsync`/`SubmitBlockAsync` paths through
a test manager harness. It acquires real work, initializes and issues a job, rejects weak proof,
searches at most two million nonces on at most four cores within ten minutes, accepts and submits a
real candidate, checks daemon height/topoheight and `top_block_hash` against the
share's block identity, rejects duplicate proof, signals clean shutdown, and
removes its data directory. Cleanup attempts both shutdown and deletion, and
preserves any original body failure instead of replacing it with a cleanup error.
No simulator or skipped-PoW
option is used. With the environment variable absent, the fixture explicitly
skips; a skipped fixture is not live validation evidence.

The `.NET` workflow has an opt-in `workflow_dispatch` input `xelis_live=true`.
It installs the checksum-pinned daemon into runner-local temporary storage and
sets `MININGCORE_TEST_XELISD` for the primary job's full suite. Ordinary PR runs
continue to skip the live fixture. The provisioning body is ShellCheck-validated
and has been executed locally, including its checksum verification. This tests
the job-manager RPC/share/submission path; Stratum socket negotiation, payouts,
current-mainnet v3 and a production soak remain outside this fixture's claims.

Windows CI also runs `scripts/release/test-xelis-windows.ps1`, which builds the
native v1 source with MSVC and verifies both pinned upstream vectors twice.
The local Windows fixture passed with v142; CI selects v143 by default. It uses
the scalar stage with unchanged hash math. The existing bundled Windows
`libmultihash.dll` lacks `xelis_hash_export`, so the managed known-answer test
remains Linux-only. Source-vector success does not certify that bundled Windows
pool runtime; updating and packaging its Xelis ABI remains #120 work.
The job manager now refuses startup unless the process is x64 and the native
library exports both `xelis_hash_export` and `xelis_hash_v2_export`. The ARM v1
stub is never invoked by a configured Xelis pool. Windows CI includes managed
startup/ABI and RPC timeout/compatibility tests alongside the source-built vectors.

## Initial lab results, 25 October 2026 (before review hardening)

Ubuntu WSL lab, native libraries built from source, official checksum-verified
v1.25.0 daemon:

| Check | Result |
| --- | --- |
| Managed build | Zero warnings and zero errors |
| Complete native build | Completed; no compiler warnings found in the build log |
| Focused Xelis target, RPC, buffer and live-daemon tests | **82 passed, zero failed, zero skipped** |
| Live scenario | Real difficulty-30,000 v1 proof accepted by stagenet; weak and duplicate proofs rejected; daemon shut down cleanly |
| Documentation links | 47 Markdown files validated |
| Full lab suite with coin daemons, PostgreSQL and Xelis enabled | 5,106 passed, 20 failed, 16 skipped |
| Clean `dev` baseline at `28f358825` | All 20 failing tests reproduced in a separate checkout (4 + 7 + 9 test runs) |

The complete suite is **not green**. The baseline failures concern existing
PostgreSQL/PPS migration fixtures: the migration requires a separate
administrator identity and some ownership/role fixtures lack privileges;
transaction cleanup can mask the original error with SQLSTATE `25P02`.
No Xelis test failed. The accounting/migration source and these baseline tests
are unchanged by this correction. Results are recorded as a regression limit,
not waived or represented as successful financial validation.

The first live attempt exposed the v1 buffer defect described above and aborted
the test host. After correcting the wrapper, both the focused run and the full
suite's live Xelis fixture passed with actual PoW verification enabled.

## Review finding resolution

| Finding | Resolution and coverage |
| --- | --- |
| P1 shared solved-header race | Per-share returned header, direct immutable submission argument, deterministic two-worker submission barrier and identity checks |
| Conditional same-prefix difficulty/algorithm refresh | Exact cached parameter comparison; same-prefix change and unchanged-response regressions; network statistics refreshed at unchanged height |
| M1 silent RPC failures and header null dereference | Error-level bounded diagnostics for either RPC/error/null response; header failure short-circuits miner-work acquisition; malicious-message redaction tests |
| M2 unsupported algorithm fallback | Canonical v1/v2 whitelist at initialization and refresh, cached dispatch, v3 rejection before both initial and replacement work publication |
| Large unquoted numeric input | Pre-parser numeric token bound, bounded declared/chunked body reads, full-U256/escaped-string acceptance and cancellation regressions |
| L1 v1 impact understated | Release notes disclose nondeterministic proof validation, memory corruption, and the historical-data limitation |
| L2 block identity | Live daemon `top_block_hash` equals the accepted share's hash |
| L3 component-only live path | Live fixture now uses production serializer registration, RpcClient, refresh/issuance and manager share/block submission; socket and payout claims remain explicitly limited |
| L4 cleanup masking | Body failure retained, both cleanup actions attempted, clean-exit assertion only after successful body; fault-injection regression |
| N1 duplicate/dead target APIs | Unused double overload and CheckDiff implementations removed; production full-width inclusive comparison retained |
| N2 unused header difficulty | Required exact value participates in header/miner-work consistency checks before publication |
| N3 telemetry precision | Remaining doubles and full Windows ABI modernization recorded as #120 follow-up; consensus target arithmetic stays exact |
| N4 relative PR documentation link | PR description uses an absolute repository blob URL |
| Additional Windows native coverage | Source-built MSVC vectors in CI and local v142 validation, with bundled runtime limitations disclosed |
| Gated live CI | Manual `.NET` workflow input installs the checksum-pinned daemon; local provisioning and ShellCheck validation |

One serial real-PoW search reached its five-minute time budget without a solution.
The fixture now searches disjoint nonce ranges on at most four cores under a
ten-minute deadline, keeping actual daemon proof verification enabled. The timeout
also exercised the cleanup path: its original cancellation exception was retained.

First-review hardening validation on 25 October 2026:

- Managed build: zero warnings and zero errors. Rebuilt Linux multihash and
  portable/AES-NI equivalence checks passed their warning audits.
- Final RPC-wide, proof-boundary and Xelis regression run: **364 passed, zero
  failed, zero skipped**, with the real daemon enabled after the final RPC
  compatibility adjustment.
- Windows source-built v1 fixture: both upstream vectors passed twice with MSVC
  v142. The bundled managed Windows runtime's missing export remains disclosed.
- Full lab suite: **5,140 passed, 20 failed, 16 skipped**. The failing-test set
  exactly matches the clean-dev baseline. The strengthened manager-level live
  fixture passed in that run, including block identity and clean shutdown.
- Documentation guards, all 47 local Markdown link/anchor checks, workflow YAML
  structure, ShellCheck and the checksum-pinned optional CI provisioning step passed.

## Second-review lifecycle and compatibility hardening

| Finding | Resolution and regression evidence |
| --- | --- |
| P1 previously issued work survives unsupported transitions | Permanent epoch retirement for all recent jobs, new authorization denial, rejection before hashing, overlap-with-verification test, and supported recovery without reviving old jobs |
| P2 / N1 header/work algorithm consistency | Pinned daemon RPC contract checked; both fields compared at initialization and refresh; deliberate mismatch fixtures retire work |
| P3 RPC CLR signature compatibility | Original generic method preserved; separate bounded API; reflection/runtime invocation of the original signature |
| R1 graceful cancellation logged as Error | Token checked before diagnostics; cancellation during either RPC is silent and does not increment outage counts |
| R2 outage logging volume | First failure plus counted recovery, with a forced diagnostic on a new incompatibility transition |
| R3 runtimes unable to hash announce work | x64 and both-export startup guard, with unsupported architecture/missing-export and actual Windows startup rejection coverage |
| Elapsed HTTP timeout | Short-lived instance transport stalls after headers; deadline is observed as timeout rather than caller cancellation |
| N2 non-JSON HTTP error page classification | Non-2xx status preserved without bypassing numeric bounds; dashes-only and oversized-numeric fixtures |
| N3 Windows v143 and opt-in live CI | Current-head Windows and manual live CI evidence is recorded in PR #221 |

Local verification before the final CI gate: **593 passed, zero failed, zero
skipped** across Xelis, RPC, snapshot and assignment/authorization regressions
with the real daemon enabled. Windows startup and RPC compatibility/timeout
regressions: **5 passed, zero failed, zero skipped**. Managed builds produced zero
warnings and zero errors. Full local baseline limitations remain disclosed above.

The complete second-review lab run finished with **5,154 passed, 20 failed,
16 skipped** (5,190 total). Its failing-test names exactly match the 20 failures
reproduced on clean `dev`; no new failures appeared. The real-daemon Xelis test
passed in this run.

The first updated PR run exposed a separate Stratum diagnostic fixture race:
it checked successful authorization after disposing TCP, when disconnect cleanup
may already have cleared authorization. The assertion now runs before disposal;
all **92** Stratum diagnostic tests pass locally. CI is rerun on that correction.

## Miner-facing suspension review

| Finding | Resolution and regression evidence |
| --- | --- |
| M1 retired-work rejection bans honest miners | Dedicated internal Stratum exception, preserved during retained-job lookup after recovery; five submissions past a ban threshold leave valid/invalid counters unchanged and never call the ban manager; an unknown job still counts as invalid |
| L1 / observation A miners remain stranded | First suspension disconnects all registered sessions without IP bans; real TCP authorization, suspension, closure, supported recovery and reauthorization are covered |
| L2 address RPC load during suspension | Subscribe and authorize disconnect before daemon calls; TCP fixtures assert no additional daemon requests |
| N1 long suspension is easy to miss | At most one reminder per ten minutes of suspended refresh failures; controlled-clock test verifies suppression, reminder count and counted recovery |
| N2 share accounting admission boundary | Final epoch check documented as the admission point; already admitted shares/blocks retain outcomes after retirement |
| N3 suspended VarDiff sentinel | Pool comment and operator notes explain why the null-job unbounded sentinel cannot admit work |
| N4 unrelated Stratum fixture change | Earlier standalone test commit only moves the assertion ahead of disconnect cleanup; it changes no runtime behavior |
| Observation B downstream Xelis APIs | Explicit source/binary breaking-change and migration table above and release-note notice |
| In-flight accepted block receipt | Submission RPC barrier spans retirement; accepted receipt retained, unrelated obsolete share rejected before hashing |

Local verification: **757 passed, zero failed, zero skipped**, covering Xelis,
RPC, snapshots, concrete pool assignments and Stratum diagnostics with the real
daemon enabled. Windows startup, RPC and all miner-suspension pool tests:
**10 passed, zero failed, zero skipped**. Windows CI includes those same pool
tests. Documentation guards, all 47 Markdown link/anchor checks and Stratum
diagnostic source guards pass. Current-head automatic and manual live CI evidence
is recorded in PR #221 when complete. Earlier full-lab baseline limitations above
remain applicable; they are not represented as successful financial validation.

## Final operational review hardening

| Finding | Resolution and regression evidence |
| --- | --- |
| N1 authorization overlapping suspension logs Error | Catch the dedicated retirement marker in authorization, perform terminal assignment cleanup quietly, and release the gate; a deterministic barrier retires work between the last check and job creation |
| N2 first anomalous consensus response disconnects sessions | Retain immediate fail-closed suspension; document the pinned-daemon rationale and verify one sweep per epoch, with a new sweep possible only after supported recovery |
| N3 repeated reconnects during long suspension | Document expected reconnect churn and miner failover; suspended listener admission closes TCP before Stratum setup or admission capacity acquisition |
| P3 listener admission | Actual listener/TCP test verifies rejection without a request, zero admission occupancy/identities or dispatch tasks, no daemon calls/ban/Error, and subscription success on the same listener after recovery |
| P3 wall-clock diagnostic timing | Use monotonic `TimeProvider` timestamps; controlled forward/backward wall-clock jumps neither trigger early reminders nor prevent the ten-minute reminder and counted recovery |

Local verification: **781 passed, zero failed, zero skipped** across the expanded
Xelis, RPC, snapshot, concrete assignment, Stratum diagnostic and listener tests,
with the real disposable daemon enabled. Windows startup/RPC and all Xelis pool
operational tests: **12 passed, zero failed, zero skipped**. Documentation and
Stratum source guards pass. Final current-head automatic and manual live CI run
links and results are recorded in PR #221 after completion. Existing #120
modernization/certification limits and the historical local database-fixture
baseline remain documented above.

## Admission classification and timestamp review

| Finding | Resolution and evidence |
| --- | --- |
| Optional refusal logs Error if recovery wins cleanup race | A dedicated internal local-admission exception preserves the refusal decision independently of current admission state; Xelis retirement derives from it. The accept handler uses that marker without depending on the Xelis family |
| Preserve unrelated setup failures | Paired real-listener regressions reopen admission before refusal cleanup, verify expected refusal is Debug and unrelated failure is Error, release admission capacity and allow exclusive rebinding |
| Timestamp-only freshness follow-up | Pinned source review and real-daemon test above confirm the original timestamp's proof remains eligible after newer timestamped work is obtained; production refresh semantics are retained |
| Long RPC outage monitoring | Operator notes distinguish last successful validated acquisition from job-ID/height changes and retain the monitoring limitation |

Expanded Linux live verification: **783 passed, zero failed, zero skipped**.
Windows startup/RPC/Xelis pool/listener verification with the repository's
existing CI filter: **33 passed, zero failed, one skipped**. An initial unfiltered
Windows run also exercised the PFX certificate-identity case already excluded
from Windows CI; that case failed its certificate thumbprint assertion. The
filtered result does not certify that excluded case. Current-head automatic and
manual real-daemon CI evidence is recorded in PR #221 after completion.

### CI installer availability

The first CI run of the admission-classification revision failed before
compilation: floating `10.0.x` setup selected SDK `10.0.403` and its LTS runtime
probe selected `10.0.14`, whose download endpoints returned HTTP 409. The
[official published release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
still listed SDK `10.0.402` / runtime `10.0.13`; these were used by the preceding
green runs, and Linux x64, Windows x64 and macOS ARM64 SDK assets returned HTTP
200 during verification. The setup steps in .NET, Release and Stratum portability
workflows now request that published SDK explicitly. Advance these installer pins
together when new servicing binaries are published and available; the application's
SDK minimum and roll-forward policy are unchanged.

The pinned `setup-dotnet` action probes the latest LTS runtime before installing
the requested SDK. Its own implementation treats failure of that optional probe
as a warning: the explicitly requested SDK includes its runtime and must still
install successfully. Current-head CI validates the resulting toolchain.

### Independent SDK maintenance and explicit refusal markers

The shared SDK installer repair is maintained independently in
[PR #222](https://github.com/NINJAK1DD/miningcore/pull/222), based directly on `dev`
and changing only the three workflows. Each workflow now declares one
`CI_DOTNET_SDK` value; all its setup steps reference that value, so future
servicing updates change three version declarations. Dependabot action-SHA
updates do not update these inputs. Advance the declarations together after
checking published servicing binaries; keep this CI-only change separate from
the Xelis runtime correction.

The admission exception comment clarifies that only the accept handler
interprets its base marker: a subtype may also be used by share submission or
authorization without implying that the current listener is closed. The
remaining generic `StratumException` fallback is removed. Both known local
refusal producers (Xelis suspension and Bitcoin BLAKE2b isolation at connection
setup) now use explicit markers. Ordinary setup exceptions, including a generic
`StratumException` while admission is closed, remain Error diagnostics.

Local verification: **784 passed, zero failed, zero skipped** in the expanded
live Xelis/RPC/assignment/listener suite. Separate Bitcoin BLAKE2b
startup/isolation/difficulty-budget/connection-churn coverage: **261 passed,
zero failed, one skipped** (the optional daemon commissioning fixture was not
enabled in this local run). Windows checks using the existing CI exclusions:
**34 passed, zero failed, one skipped**. Workflow/action-pin, YAML structure,
documentation and diagnostic-source checks pass. Independent SDK and final Xelis
CI evidence are recorded on their respective PRs after completion.
