# Integration deadline test scheduling

`IntegrationDeadlineCollection` uses xUnit's collection-level
`DisableParallelization` setting for `ProgramPoolTemplateTests` and
`MergedMiningManagerReorgTests`. It changes test scheduling, not production
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

The whole of both selected classes is isolated, including pure unit cases. This
avoids a large fixture extraction and its review risk, but can lengthen the serial
phase. Split pure cases into separate classes if measurements show material cost
or the fixtures grow substantially; raw test counts are not a runtime measurement.

In the pinned xUnit 2.4.2 runner, non-parallel collections run after the parallel
group. The assembly's concurrency synchronization context still applies: this
removes cross-collection contention, not every possible delay within one test.
If a timeout recurs, inspect host/IO progress and intra-test continuation scheduling
before considering a test watchdog change. Do not automatically relax deadlines.
Revisit this isolation when upgrading xUnit to its conservative scheduler.

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
