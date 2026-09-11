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
set; they do not use timing-sensitive competing-test probes.

References: [xUnit scheduling documentation](https://xunit.net/docs/running-tests-in-parallel)
and the [pinned 2.4.2 assembly runner](https://github.com/xunit/xunit/blob/v2-2.4.2/src/xunit.execution/Sdk/Frameworks/Runners/XunitTestAssemblyRunner.cs).
