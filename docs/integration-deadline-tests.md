# Integration deadline test scheduling

`IntegrationDeadlineCollection` uses xUnit's collection-level
`DisableParallelization` setting for `ProgramPoolTemplateTests`,
`MergedMiningManagerReorgTests`, and `BitcoinPublicationFailureTests`.
It changes test scheduling, not production
deadlines or test assertions. The definition and its contract tests live at the
test-project root because the collection spans the root and Blockchain namespaces.

## Membership and limits

Membership is deliberately evidence-based: these fixtures exhibited delayed
continuations in full-suite CI during generic-host lifecycle and loopback JSON-RPC
tests. A short watchdog alone does not qualify another class. Before adding one,
record the failing scenario, distinguish scheduler delay from a product defect,
and measure whole-suite runtime. The exact-membership contract requires review
both when a member is removed and when another class is added.

`ConnectionFactoryExtensionsTests` uses controlled/mock database connections;
its short waits do not demonstrate the same real-host/RPC failure.
`ShareReceiverTests` exercises ZeroMQ and remains parallel: there is no equivalent
observed failure motivating isolation here. `RpcSubscriptionLifetimeTests` also
remains parallel. Investigate new failures in these classes before changing their
scheduling; do not infer immunity from their omission.

The selected classes are isolated as units. Most retain pure cases because a
large fixture extraction has review cost, but these cases can lengthen the serial
phase. The four pure publication fail-stop cleanup cases now run in the separate,
parallel `BitcoinPublicationCleanupTests` class, alongside the registry-lifetime
contract. Its TCP construction-race cases remain in the deadline fixture. Consider
further extraction if measurements show material cost or fixtures grow
substantially; raw test counts are not a runtime measurement.

In the pinned xUnit 2.4.2 runner, non-parallel collections run after the parallel
group. The assembly's concurrency synchronization context still applies: this
removes cross-collection contention, not every possible delay within one test.
If a timeout recurs, inspect host/IO progress and intra-test continuation scheduling
before considering a test watchdog change. Do not automatically relax deadlines.
Revisit this isolation when upgrading xUnit to its conservative scheduler.

## Bitcoin publication fixture scheduling

At `193733c0a`, the [full .NET CI suite](https://github.com/NINJAK1DD/miningcore/actions/runs/35461023613/attempts/2)
timed out in `BitcoinPublicationFailureTests.FullSendQueue_ResponseOrNotificationFailure_NeverRetries`
while awaiting disconnect after a saturated-queue configure request. Other completed
tests in the same log show delayed continuations. Focused runs and the unconstrained
Linux suite had passed. This is consistent with scheduler contention, not a trace
proving which continuation was delayed.

A whole-assembly comparison on 2026-09-19 used Ubuntu 22.04 WSL, process-local
`DOTNET_PROCESSOR_COUNT=2`, identical native libraries and Bitcoin Core 28.1, with
PostgreSQL unset. The baseline reproduced the same publication-fixture watchdog
failure for a saturated-queue suggestion request. The patch admits only this
fixture to the existing collection; all 37 cases move together, including its
small unit cases. Neither production behavior, watchdogs nor protocol assertions
change. The exact-membership contract pins the addition.

| Run order | Scheduling | Passed | Failed | Skipped | Publication passed/failed | TRX elapsed seconds |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | Baseline `193733c0a` | 3336 | 4 | 41 | 36 / 1 | 277.02 |
| 2 | Scoped collection patch | 3334 | 6 | 41 | 37 / 0 | 278.11 |
| 3 | Repeat at `b9d856471` | 3334 | 6 | 41 | 37 / 0 | 274.99 |
| 4 | `8daaecf3b`, cleanup cases extracted | 3348 | 2 | 41 | 42 / 0 | 276.65 |
| 5 | Same `8daaecf3b`, publication membership reverted | 3346 | 4 | 41 | 41 / 1 | 275.69 |
| 6 | BLAKE2b/domain closure at `04f9a23c5` | 3348 | 4 | 41 | 42 / 0 | 276.63 |
| 7 | Independent BLAKE2b broadcast cleanup/reporting | 3347 | 7 | 41 | 42 / 0 | 278.89 |

The initial measured whole-suite cost was +1.09 seconds (about 0.4%). The repeat
was 2.03 seconds faster than baseline. This small sample cannot prove flake
elimination or a causal runtime difference. The first three runs retain failures outside
the selected fixture: Bitcoin notification snapshots, BLAKE2b admission and Stratum
listener shutdown. The patched run also failed hosted metrics startup, payout
commit-admission and pool startup deadlines. The repeat reproduces all six patched
failures, including those three additional failures; it does not establish that
they are noise or causally independent of the scheduling change. These fixtures
remain parallel and require separate diagnosis. None of those first three
constrained full-suite runs is claimed as green.
The change targets the reproduced publication fixture without expanding collection
membership to unrelated tests. Normal full-suite CI remains a separate required check.

Local evidence is retained in `build/issue183/results/deadline-baseline-1.trx` and
`deadline-patched-1.trx` and `deadline-patched-2.trx`, with matching logs under
`build/issue183/`. The repeat used the same `b9d856471` binaries before the later
terminal-refusal fix, new race regressions and pure-cleanup extraction were built,
so it measures the original scheduling change on the same 37 cases. These ignored
local artifacts are not included in the PR; the table is a measurement summary.

The next controlled comparison measured `8daaecf3b` with its 42 publication cases
and five parallel cleanup cases. The membership-reverted run changed only the
publication class's collection attribute and the matching expected-member entry;
production code, assertions, watchdogs, dependencies and process-local two-CPU
setting were identical. Both were whole-assembly runs. The temporary membership
change was restored before implementing the subsequent BLAKE2b closure follow-up.

With membership retained, notification snapshot delivery and pool shutdown timed
out. With membership reverted, metrics export, payout completion and Stratum
admission timed out; a publication construction/ban race also failed with a broken
pipe while sending its test submission. The scoped fixture passed 42/42 versus
41/42 with membership reverted. This pair supports retaining its narrow isolation,
but does not establish a general runtime or reliability benefit for other fixtures.

Triage of the three previously additional failures:

- `MetricsPublisher_ExportsBoundedShareAccountingOutcomes` exhausted its shared
  cancellation deadline while exporting metrics. It passed at `8daaecf3b` with
  membership retained and failed with membership reverted, so the failure is not
  specific to the publication collection addition.
- `IsolatedFault_AfterCommitAdmissionRetainsOwnedDatabaseTransition(Orphaned)`
  timed out awaiting the payout cycle after releasing its controlled repository
  barrier. It likewise passed with membership retained and failed with membership
  reverted. This is a completion timeout, not a failed accounting assertion.
- `RunAsync_WithoutInternalStratum_RemainsOnlineUntilCancellation` timed out
  awaiting shutdown after cancelling the pool. It failed with membership retained
  and passed with membership reverted in this pair. That association remains
  unresolved; one pair does not establish that membership caused the timeout.

These outcomes do not show three deterministic new failures caused by the
collection change, nor do they prove contention is their only cause. Keep normal
full-suite CI required; do not broaden isolation or relax
watchdogs on this evidence. Local comparison artifacts are
`deadline-head-1.trx` and `deadline-membership-reverted-1.trx` in the same ignored
results directory, with matching logs.

A focused two-CPU run on the subsequent BLAKE2b closure follow-up passed all four
cases from the three scenarios above (the payout test has two theory rows), with
zero failures or skips. This checks their basic behavior under the same processor
limit, but deliberately omits whole-assembly contention and cannot establish a
root cause for their intermittent full-suite deadlines. Its local evidence is
`deadline-triage.trx` and `deadline-triage.log`.

Row 6 measures the production and test state at `04f9a23c5`,
including the public domain exception and both new BLAKE2b broadcast regressions.
Both BLAKE2b cases passed in their existing parallel fixture; all 42 publication
cases passed in the deadline collection. Four failures remained: idle Stratum
listener shutdown, pool shutdown without internal Stratum, the orphaned payout
completion case, and notification snapshot delivery. Metrics export passed.
That constrained run is not green and does not resolve those other
fixtures' deadline behavior. Its evidence is `deadline-final-1.trx` and
`deadline-final-1.log`; it is distinct from the reviewed-head comparison and the
focused four-case triage run. Later independent BLAKE2b broadcast-failure cleanup
and reporting changes are not represented by this historical row.

Row 7 measures the subsequent independent BLAKE2b broadcast-failure cleanup and
reporting implementation, including both new live-registry failure cases. All
four BLAKE2b broadcast cases passed in their existing parallel fixture, as did
all 42 publication cases. Seven failures remained: Stratum request cancellation
did not throw the expected cancellation exception; the BLAKE2b malformed numeric
string case (`"2,0"`) disconnected before its expected response; metrics export,
notification snapshot delivery, idle listener shutdown, pool shutdown and
orphaned payout completion exhausted their deadlines. This constrained run is
not green and does not establish the causes of those failures. No watchdog,
assertion or collection membership was changed. Its evidence is
`deadline-round6-final-1.trx` and `deadline-round6-final-1.log` in the same ignored
local artifact directories. No production or test changes followed this run.

## Background-service startup context

The [post-merge dev run at `ac903306`](https://github.com/NINJAK1DD/miningcore/actions/runs/34797126540)
failed only `BtStreamReceiverTests.StartAsync_CompletesImmediatelyWithoutEndpoints`
at its ten-second watchdog (2,848 passed, one failed, one skipped). All live
PostgreSQL timeout/payout cases passed. The empty-endpoint path signals readiness
and returns without network I/O, but `StartupGatedBackgroundService.StartAsync`
captured its caller's synchronization context while awaiting that result. An
occupied context can delay reporting readiness even after background work finishes.
The CI trace is consistent with this scheduling dependency; it does not contain
a scheduler trace proving which continuation was delayed.

The gate's internal awaits now use `ConfigureAwait(false)`: readiness, failure
and cancellation have no caller-context affinity. This leaves test collection
membership, watchdogs and the readiness contract unchanged. The deterministic
`StartupGatedBackgroundServiceTests` holds execution behind a controlled gate,
records posts to the caller context, and covers readiness, early execution failure,
return without readiness, explicitly signaled failure and cancellation. All five
cases failed the context-post assertion before the fix; no timing race was needed.
The recording context forwards callbacks so a failing assertion does not leave
startup suspended. Local baseline results are in
`src/Miningcore.Tests/TestResults/startup-context-before.trx`.
After the fix, **85 targeted tests passed**, with no failures or skips, on the
Windows lab: the five context cases plus `BtStreamReceiverTests`,
`HostedServiceStartupTests`, `ShareReceiverTests` and `ProgramPoolTemplateTests`.
The managed build had zero warnings/errors. Results are in
`src/Miningcore.Tests/TestResults/startup-context-after.trx`; full-suite CI remains
the separate check for behavior under assembly-wide contention.

## Verification expectations

Filtered repetitions check fixture stability but cannot reproduce competition from
the rest of the assembly. For stronger evidence, run the entire assembly before
and after with `DOTNET_PROCESSOR_COUNT=2`, using identical native dependencies and
external test services. Alternate multiple baseline/patched runs, retain TRX
results, report failures and skips as well as elapsed time, and distinguish
filtered results from whole-suite measurements. A clean baseline does not prove
that a previously intermittent failure cannot happen.

The collection contracts share `CollectionAssertions` with the existing RPC
global-logging isolation check. They resolve each fixture to its actual collection
definition, require a unique non-parallel definition, and pin the reviewed member
set, including inherited collection membership and nearest-derived overrides.
The resolver conservatively includes attributed types even if they currently
declare no test methods. Synthetic resolver tests use runtime-only assemblies so
they cannot add collections to xUnit's discovery. `NonParallelCollectionTests`
guards the five logging collections, the administrative API environment collection
and the Bitcoin Core payout integration collection. Together with the dedicated
deadline contract, all eight current non-parallel collections have exact-member
guards. An assembly-wide definition inventory also requires any new non-parallel
collection to be added explicitly to the reviewed set. These metadata checks run
even when optional integration tests are skipped;
they neither start daemons nor change collection scheduling. The parallel
`PayoutManagerLeaseIntegrationCollection` is intentionally excluded.
Duplicate definitions and unsupported constructor
metadata fail assertions rather than silently weakening the contract. These
guards do not use timing-sensitive competing-test probes.

The synthetic tests require a dynamic-code-capable runtime and preserved reflection
metadata. They assert the dynamic-code requirement before using `Reflection.Emit`.
That assertion does not detect trimming or prove reflection metadata preservation;
trimmed test assemblies are not currently supported or verified by these guards.
A future Native AOT or trimmed test lane needs an explicit fixture/metadata design
review, not silently skipped guards. The small shared emitted-type builder is
retained because malformed and duplicate collection definitions must be tested
without contaminating the actual discovery assembly; no general-purpose test
framework or new package is needed. There are 15 contract cases across the three
contract test classes: two deadline facts, seven non-parallel membership cases,
one definition-inventory fact and five resolver regression facts.

## Measured whole-assembly comparison

On 2026-09-11, three alternating baseline/patched pairs ran on Ubuntu 22.04 WSL
with process-local `DOTNET_PROCESSOR_COUNT=2`, identical native libraries, and
`MININGCORE_TEST_POSTGRES` unset. Baseline was `dev` at `f17ba085`; patched was
`a979340e`. Every run had 28 skipped cases (counted from TRX test outcomes, not
the runner's misleading zero `notExecuted` summary counter). This is a constrained
local comparison, not a replacement for database-backed CI.

| Run order | Revision | Passed | Failed | Skipped | Elapsed seconds |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Baseline | 2553 | 6 | 28 | 165.40 |
| 2 | Patched | 2558 | 3 | 28 | 174.00 |
| 3 | Baseline | 2554 | 5 | 28 | 165.12 |
| 4 | Patched | 2560 | 1 | 28 | 180.41 |
| 5 | Baseline | 2554 | 5 | 28 | 165.55 |
| 6 | Patched | 2557 | 4 | 28 | 174.31 |

Median elapsed time was 165.40 seconds baseline versus 174.31 seconds patched
(+8.91 seconds, approximately 5.4%). Failures and the small sample limit causal
performance claims. Retain whole-class isolation for this narrow change: the
observed cost does not justify extracting established fixtures here. Reassess if
representative CI measurements show a material regression.

Both targeted classes passed in all three patched runs. Baseline timed out in
`MergedMiningManagerReorgTests.AuxiliaryRefreshTimeout_RetainsCacheAndPublishesDegradedState`
twice and `ProgramPoolTemplateTests.CandidatePersistenceFailure_StopsRealHostAndDrainsSiblingPool`
once. This supports the scoped scheduling change but does not prove flake elimination.

Failures remained outside those classes in `PayoutManagerTests`,
`HostedServiceStartupTests`, `BtStreamReceiverTests`, `PoolBaseTests` and
`StratumServerTests`; every affected class also failed in the baseline. Those
failures require separate diagnosis, not automatic collection expansion or raised
timeouts. None of the six complete-assembly runs is claimed as green. Raw TRX
files are retained locally in `build/deadline-comparison/`. That directory is
gitignored: the raw local evidence is not included in this repository or PR, so
the table alone is not an independently auditable artifact bundle.

References: [xUnit scheduling documentation](https://xunit.net/docs/running-tests-in-parallel),
the [pinned 2.4.2 assembly runner](https://github.com/xunit/xunit/blob/v2-2.4.2/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestAssemblyRunner.cs),
the [inheritable collection attribute](https://github.com/xunit/xunit/blob/v2-2.4.2/src/xunit.core/CollectionAttribute.cs)
and [attribute inheritance resolution](https://github.com/xunit/xunit/blob/v2-2.4.2/src/xunit.execution/Sdk/Reflection/ReflectionAttributeInfo.cs).
