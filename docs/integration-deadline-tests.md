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
all 42 publication cases. Two assertion failures require separate triage: Stratum
request cancellation did not throw the expected cancellation exception, and the
BLAKE2b malformed numeric string case (`"2,0"`) encountered EOF during its initial
subscription. Five other failures exhausted deadlines: metrics export,
notification snapshot delivery, idle listener shutdown, pool shutdown and
orphaned payout completion. This constrained run is not green. No watchdog,
assertion or collection membership was changed. Its evidence is
`deadline-round6-final-1.trx` and `deadline-round6-final-1.log` in the same ignored
local artifact directories. It predates the test-only assertion triage below.

### Separate triage of the row 7 assertion failures

`ProcessRequest_Honor_CancellationToken` invokes `ProcessRequestAsync` directly
on a standalone connection. It does not run `DispatchAsync`, the receive pipe,
startup admission or a broadcast. Both this helper's handler invocation and the
original test were unchanged from base `2702579ca`. The original handler waited
on a one-second delay while a separate 20-millisecond timer requested cancellation.
It therefore assumed cancellation would win against normal completion.
[Timer callbacks run on thread-pool threads](https://learn.microsoft.com/en-us/dotnet/api/system.threading.timer?view=net-10.0),
and [task cancellation is cooperative](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation).
The retained failure has no callback-order trace; it does not prove swallowed
cancellation or establish why cancellation lost that run.

At `407377801`, the regression waited for handler entry, explicitly canceled the
supplied token, and required the handler's infinite cancellable delay to propagate
`TaskCanceledException`. It also verified token identity, canceled task state
and exactly one handler call, using the existing ten-second test watchdog. The
whole-suite follow-up below further removes its continuation-order dependency.
A temporary mutation that swallowed `OperationCanceledException` failed the
exception assertion as expected. The mutation was removed before final testing.
This removes the competing-timer assumption without weakening cancellation
coverage or changing production behavior.

The `MalformedNumericStrings_ConsumeBudgetThenDisconnect("2,0")` failure's
retained stack is `ReadAsync -> RequestAsync -> Subscribe`, at the initial
subscription on line 346 of `BitcoinBlake2bAdmissionHardeningTests.cs` at
`081b2dcdc`. No `mining.configure` or `"2,0"` value had been sent, so this occurrence
cannot show culture-dependent parsing or an early difficulty-budget refusal.
The BLAKE2b configure parser explicitly uses `NumberStyles.Float` and
`CultureInfo.InvariantCulture`. The test transport has a startup deadline, but
the original assertion recorded no completion reason: startup expiry remains
a hypothesis in that original log, not a proven cause of this EOF. The
follow-up below reproduces and classifies startup expiry. The harness includes request
ID, transport completion reason, dispatch completion, harness/request cancellation,
disconnect latch and response count in an EOF assertion for future diagnosis.

Focused Ubuntu 22.04 WSL runs used the same `DOTNET_PROCESSOR_COUNT=2`, existing
native libraries and .NET 10 binaries; no daemon or database dependency was needed:

| Selection | Passed | Failed | Skipped | Local TRX |
| --- | ---: | ---: | ---: | --- |
| Original cancellation test | 1 | 0 | 0 | `assertion-cancellation-before.trx` |
| Original malformed-number theory alone | 5 | 0 | 0 | `assertion-malformed-alone.trx` |
| Entire original BLAKE2b difficulty fixture, including culture cases | 177 | 0 | 0 | `assertion-culture-fixture.trx` |
| Ordered culture probe | 3 | 0 | 0 | `assertion-ordered-cultures.trx` |
| Deterministic cancellation regression | 1 | 0 | 0 | `assertion-cancellation-after.trx` |
| Deliberately swallowed cancellation (negative control) | 0 | 1 | 0 | `assertion-cancellation-mutant.trx` |
| Final Stratum/BLAKE2b/publication and collection-contract selection | 251 | 0 | 0 | `assertion-focused-final.trx` |

The temporary ordered probe ran three rows (`fr-FR`, `de-DE`, `tr-TR`). Each
awaited the existing invariant-number test, date-scalar compatibility test and
canonical legacy-culture test in that order, verified restoration after each,
then ran the `"2,0"` budget theory. It repeated that theory with the selected
culture actively installed. All subscription, error-response and exact-budget
assertions passed. The probe was removed afterward rather than duplicating
those tests in the permanent suite. These results do not reproduce a culture
leak; they do not prove that the unexplained subscription EOF cannot recur.
The ignored TRX files above and matching logs are retained under the same local
artifact directories. Normal full-suite CI remains a separate required check;
none of this focused evidence turns row 7 into a green whole-suite result.

### Run 7 assertion-failure investigation

The 2026-09-20 whole-suite follow-up was measured in an isolated worktree based
on `081b2dcdc`, then integrated with `407377801` after that concurrent update. The original log and TRX
establish different failure phases; neither assertion is classified as generic
contention noise:

- `ProcessRequest_Honor_CancellationToken` reported **no exception**, not the
  wrong exception type. It invokes `ProcessRequestAsync` directly through
  `PrivateObject`; no socket, receive loop, broadcast or `DispatchAsync` error
  selection participates. Its handler awaits a one-second `Task.Delay`, while a
  separate 20 ms timer requests cancellation. If both timers become runnable
  before the worker processes them, normal delay completion is possible. The
  failed assertion establishes that this handler completed normally; the log
  does not trace timer callback scheduling. The revised test verifies that the
  handler receives the original, uncancelled token, cancels inside that handler,
  and returns an infinite delay using that
  token. It requires `TaskCanceledException` with the exact token and one handler
  invocation. Cancellation occurs after handler admission with no timer or
  continuation ordering deciding its outcome. The receive-loop cancellation
  regression remains separate and unchanged.
- `MalformedNumericStrings_ConsumeBudgetThenDisconnect("2,0")` failed in
  `Subscribe` -> `RequestAsync` -> `ReadAsync`, at the initial subscription on
  line 346 of the reviewed test. **No malformed configure request had been sent.**
  Consequently neither parsing `"2,0"` nor consuming its difficulty budget caused
  that failure. The recorded test lasted 41 seconds and the dispatcher has a
  ten-second first-request deadline, making startup expiry a concrete hypothesis.
  The old EOF assertion printed an empty dispatch error: startup expiry reports
  normal completion, and EOF can also precede the terminal callback. That log
  alone does not prove the completion reason. A follow-up whole-suite diagnostic
  run reproduced this exact row and phase with **`completion=StartupTimeout,
  request=1, subscribed=False, error=`**. This establishes startup expiry for
  the reproduced failure, before any configure parsing or budget consumption.
  It does not identify which startup continuation was delayed. The fixture now
  awaits bounded dispatch completion on EOF and includes its completion reason, request number,
  subscription state and error in the failure. It still fails on premature EOF.

Before edits, a focused Linux run at `081b2dcdc` with
`DOTNET_PROCESSOR_COUNT=2` passed the cancellation case and all five malformed
numeric rows (**6 passed, zero failed/skipped**). The final hardened tests,
diagnostics and culture regression under the same processor limit passed **231 cases, zero failed/skipped** across
`StratumConnectionTests`, `BitcoinBlake2bDifficultyBudgetTests`,
`BitcoinPublicationFailureTests` and `IntegrationDeadlineCollectionTests`.
The new culture regression explicitly runs both date-scalar culture cases, the
German invariant-number scenario and the canonical legacy-culture scenario in
one test, checks that each restores the caller's culture, then runs all five
malformed strings. It also repeats `"2,0"` with `fr-FR` and `de-DE` deliberately
active. Every budget response and terminal-disconnect assertion is retained.
Production configure parsing already uses `NumberStyles.Float` and
`InvariantCulture`; no parsing change is warranted by this failure.

This follow-up changes only tests and evidence. It does not change production
deadlines, test watchdogs or collection membership. Filtered success and the
explicit culture sequence do not establish reliability under whole-suite load.
The five deadline failures from run 7 remain a separate investigation; this
triage does not claim to resolve them. An intermediate cancellation-test version
used external explicit cancellation plus a ten-second continuation watchdog.
It passed focused testing but hit that watchdog in the whole-suite diagnostic
run. The final version above performs cancellation inside the handler and has
no dependency on timer order or a runner continuation to initiate cancellation.

The final whole-assembly repeat at local `0f5b2d4bf` used the same two-CPU limit, native libraries,
Bitcoin Core 28.1 and unset PostgreSQL as row 7: **3,352 passed, 3 failed,
41 skipped**. Cancellation, all five malformed-input rows, the ordered culture
regression, all 42 publication cases and all four BLAKE2b broadcast cases passed.
The remaining failures were orphaned payout completion, idle listener shutdown
and notification snapshot delivery. This run is **not green** and does not prove
startup-timeout flakiness eliminated; the reproduced startup expiry above remains
part of the evidence. Results are `build/review191/full-final.log` and
`build/review191/results/full-final.trx`. Normal CI is a separate required check.

Local baseline evidence is `build/issue183/results/round7-baseline-targeted.trx`
in the original worktree. Final focused evidence is
`build/review191/focused-final.log` and
`build/review191/results/focused-final.trx` in the review worktree. The diagnostic
run is retained in `build/review191/full.log` and its matching TRX. These artifacts
are ignored; the findings and test sequence above are included in the PR.

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
