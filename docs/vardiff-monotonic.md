# Monotonic VarDiff timing

[Issue #185](https://github.com/NINJAK1DD/miningcore/issues/185) moves shared VarDiff
intervals and retarget cooldowns from UTC to a monotonic counter. NTP corrections,
manual clock changes and daylight-saving changes cannot turn an otherwise identical
share sequence into a different difficulty calculation. This corrects timing accuracy;
proof validation, immutable job targets and accepted-share accounting keep their
existing contracts.

## Consumer audit and representation

The audit covered `VarDiffContext`, `WorkerContextBase.Init`, both manager entry points,
`PoolBase.OnConnect`, share-triggered updates, the periodic idle updater, all family
publication overrides, static-difficulty disable paths and job validation consumers.
Production construction is centralized in `WorkerContextBase.Init`; no family stores
or reconstructs an absolute elapsed timestamp independently of the shared manager.

| State | Previous representation | Current meaning and consumers |
| --- | --- | --- |
| `Created` | UTC context creation time used by idle updates | `CreatedTimestamp`: immutable counter sample taken when the VarDiff context is constructed; idle baseline before the first share |
| `LastTs` | Nullable Unix seconds rounded to milliseconds | `LastShareTimestamp`: nullable integer counter sample, advanced by a real share or an idle retarget that actually changes difficulty |
| `LastRetarget` | Unix seconds | `LastRetargetTimestamp`: integer counter sample used for retarget cooldowns; initialized at construction and restarted on first share or an actual retarget |
| `TimeBuffer` | Elapsed seconds derived from UTC differences | Elapsed seconds derived from the context's `TimeProvider.GetElapsedTime`; at most ten stored intervals |
| `LastUpdate` | Nullable UTC assignment metadata | Still UTC from `IMasterClock.Now`, written only when the manager returns a changed difficulty; previous-difficulty validation semantics are retained |
| Worker `Created`, `LastActivity` | UTC activity/accounting metadata | Still UTC; excluded from VarDiff elapsed measurement |

All concrete worker contexts inherit this timing initialization: Alephium, Beam, Bitcoin,
merged Bitcoin, Conceal, Cryptonote, Equihash, Ergo, Ethereum, Handshake, Kaspa, Nexa,
Progpow, Satoshicash, Warthog, Xelis and Zano. BLAKE2b reuses Bitcoin's worker context;
coin-specific subclasses also use the shared path. The tests discover every concrete
worker-context type so a newly added family participates automatically.

Job consumers of `LastUpdate` include Bitcoin and merged mining, Beam, Alephium,
Conceal, Cryptonote, Equihash/Veruscoin, Ergo, Ethereum/Cortex, Handshake, Kaspa and its
custom jobs, Nexa, Progpow and its custom jobs, Satoshicash, Warthog, Xelis and Zano.
These consumers do not receive monotonic values in place of their UTC metadata.
BLAKE2b validates against each job's immutable assigned target and credits its accepted
proof at that original difficulty.

## Timing, lifecycle and concurrency

Each context owns an immutable `TimeProvider`, defaulting to `TimeProvider.System`.
The counter domain cannot be replaced under existing samples. Initialization accepts an
optional provider for deterministic testing; replacement contexts get a fresh creation
sample and their own domain. Reinitializing a worker with VarDiff disabled clears its
old context. No new timers, queues or background tasks are allocated per worker.

Both producers read the counter under the same context monitor as their timing state.
Elapsed conversion uses the provider's frequency, following .NET's
[GetTimestamp](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.gettimestamp?view=net-10.0)
and [GetElapsedTime](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.getelapsedtime?view=net-10.0)
contracts and the repository's existing difficulty-request-budget pattern. Positive
submillisecond intervals are retained down to `GetElapsedTime`'s `TimeSpan` resolution
rather than being truncated to Unix milliseconds. The
[.NET 10 implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/System/TimeProvider.cs)
subtracts integer counter samples before converting the difference, avoiding loss of
small intervals from floating-point absolute timestamps. UTC is read only for the
existing assignment marker.

The first share starts its own sample/cooldown baseline without retargeting. An idle
producer before the first share uses context creation. Idle retargets require both
the full inactivity interval and the full monotonic retarget cooldown. The old one-second
inactivity allowance is removed: a sweep before either deadline waits for the next sweep.
No-op idle sweeps preserve the next
real share's baseline and buffer. Actual retargets clear the buffer and restart the
cooldown. A broken counter moving backward or poisoned interval history is defensively
rebased without changing the last assignment marker.

An idle producer skips a contended monitor. Share producers recheck cancellation and
context identity after acquiring the monitor, rejecting a context replaced while waiting
for that monitor. The monitor check alone does not protect asynchronous publication.
Every pool family therefore also uses one asynchronous worker assignment gate across
the enabled-state check, calculation and complete publication. Static/NiceHash assignments,
difficulty suggestions and pending-difficulty broadcasts use the same gate. An explicit
assignment clears deferred difficulty; replacement/disable cannot resurrect an old queue.
A fixed assignment either commits before calculation or follows a completed dynamic
publication, with the fixed assignment winning. The gate belongs to the worker lifetime,
so replacing its VarDiff context does not replace its synchronization domain.

Idle sweeps try the asynchronous gate without waiting, so a slow authorization or
broadcast cannot queue idle work behind a busy miner or delay the sweep's other workers.
The next periodic sweep retries. Share and explicit-assignment producers still wait with
their request/shutdown cancellation token. An ambient ownership lease detects recursive
acquisition for the same worker, including across awaits and child tasks, and throws
immediately. Nested assignments for different workers are also rejected before waiting:
independent operations must not acquire worker A then B and B then A in opposite orders.
Fan-out must start outside an assignment lease. Generic assignment broadcasts and the
BLAKE2b broadcast reject inherited ownership at their entry point, before per-miner
error handling; a nested broadcast fails once without disconnecting the pool's miners.
Released ownership cannot block a later
child operation, and a released lease clears its worker reference so captured contexts
do not retain the worker's jobs through stale ownership.

Background tasks, timers and subscriptions created under a lease inherit its logical
ownership through `ExecutionContext`, even when not awaited. Prefer scheduling them
after release. Deliberately detached work can use
[ExecutionContext.SuppressFlow](https://learn.microsoft.com/en-us/dotnet/api/system.threading.executioncontext.suppressflow)
only during synchronous scheduling; restore flow before any await, and never await that
work's completion while holding the lease. This opts out of all ambient execution state,
so it requires an explicit scheduling decision rather than changing the gate helper.

Daemon/NiceHash lookups and accepted-proof/accounting work stay outside the assignment
gate. `PoolBase` forwards cancellation to both producers and releases the gate on every
exit. Request tokens also reach NiceHash HTTP preparation. Bitcoin subscription's core
requires a prepared result, preventing a fallback HTTP lookup inside its gate. BLAKE2b's
failure cleanup intentionally ignores request cancellation to close a partially published
assignment. Idle sweeps suppress only cancellation of their owning shutdown token, including
effort-check cancellation; independent failures retain diagnostics. BLAKE2b's additional
terminal-publication policy remains in place on top of this shared serialization.
NiceHash lookup cancellation by the originating request returns no override without an
error log; independent API timeouts and HTTP failures retain diagnostics.

Positive-interval proportional arithmetic, overflow/underflow recovery, configured
minimum/maximum and `maxDelta` remain intact above the burst floor. Zero and tiny positive
means use at least **0.001 / min(interval count, 10)** seconds, including the current
interval. This conservative rate policy extends #184's zero-window estimate to positive
server-processing gaps in coalesced TCP bursts. Measured intervals remain unchanged in
the buffer; the floor applies only to the inferred rate. Sparse burst windows cannot claim
the same rate as full windows; tiny target intervals do not cause the floor to lower
difficulty. BLAKE2b still supplies its representable maximum without rewriting config.

## Regression and live validation

`VarDiffMonotonicTests` compare identical share/idle counter sequences across all
concrete worker contexts while moving UTC forward and backward. They cover creation,
first share, idle before first share, strict cooldown, resets, replacement/disabled
contexts, canceled operations, real monitor contention and provider-specific units.
`VarDiffManagerTests` retain arithmetic, zero-window, poisoned-history, no-op sweep and
configured-bound coverage, and explicitly measure positive submillisecond intervals.
They also cover zero, 100 ns and 20 microsecond bursts with sparse/full buffers,
small-target holds and configured/protocol bounds. `PoolBaseVarDiffTests` exercise both
post-identity-check and pre-publication race orderings across every worker context, with
immediate and deferred publication, replacement/disable, cancellation and failure exits.

The BLAKE2b wire tests cover both wall-step directions, retained jobs, representable
new work, disable-before-calculation and assignment-before-fixed-difficulty races.
Existing cancellation and publication-failure cases use deterministic monotonic
contexts. Canonical Bitcoin's publication and accepted-credit fixtures are migrated too.
The TCP harness initializes contexts through production `OnConnect -> Init`; the wall-step
wire cases inject the provider there. Separate tests verify the default System provider.
Canonical Bitcoin's authorize/configure wire tests verify the shared assignment gate.

`PoolFamilyAssignmentTests` additionally invokes each of the 16 concrete generic pool
families' actual authorization/login, idle retarget and job-broadcast methods in both
contention orderings, with a ten-second deadlock timeout and diagnostics checked for
hidden broadcast failures. After an outbound queue fence, they assert the last announced
difficulty or effective target matches the final fixed assignment, including packed Beam,
target-based Equihash/ProgPoW, Ergo job targets and CryptoNote login/job targets. This
exposed Handshake's missing static-authorize difficulty notification, now sent under its
assignment gate. The native wire assertions also exposed missing full-width target copies
in Conceal/Cryptonote, with the same padding gap in Zano. All three encoders now initialize
padding and copy full-width/signed-prefix values. Nine independent fixed vectors cover
the 33-, 32- and 31-byte representations. Eighteen sub-unit vectors additionally verify
saturation at the easiest uint256 target, including below 1/255 and the smallest positive
double. Only target encoding is saturated; configured worker difficulty and proof credit
are unchanged. Fifteen invalid-input cases reject zero, negative and non-finite difficulty.
Two generic broadcast cases and a two-miner BLAKE2b TCP case verify nested fan-out rejects
before iteration, preserves connections/work and permits a later normal broadcast.
Ready jobs and daemon address replies are fixtures; gate,
request mutation, publication and connection behavior are production code. Conceal,
Cryptonote and Zano use Linux native address/blob validation. BLAKE2b has two equivalent
TCP wire races with a response fence. Separate concrete-family cancellation tests cover
Ethereum, Conceal, Cryptonote and Zano, and a blocking HTTP fixture verifies NiceHash
request cancellation. Required reflection members and network enums are explicit per
family; a renamed member fails setup rather than silently skipping it. These complement
the worker-context matrix rather than replacing it. CI validates the full suite's TRX
with `scripts/release/test-native-family-evidence.py`; all six native contention and three
native cancellation cases must be present exactly once and pass. Negative fixtures
verify the guard rejects skipped, failed, absent, duplicated and unrecognized cases.

The daemon-backed `BitcoinBlake2bRegtestTests` use real header-v2 proofs from the pinned
Knots node and PostgreSQL accounting. New forward/backward UTC correction cases hold
the monotonic interval at five seconds and verify the same retarget, matching target
notification, original proof difficulty, usable connection and exactly-once settlement
for SOLO, PPS, PROP and PPLNS. Existing zero-window, delta-limit and terminal-publication
cases remain enabled. UTC corrections and monotonic samples are injected test inputs,
not clock changes on a running host. Deterministic tests establish clock independence;
the live daemon/database adds proof validation and settlement evidence. These tests
require both environment variables; skipped daemon
or ledger tests do not count as live evidence:

```sh
export MININGCORE_TEST_BLAKE2B_BITCOIND=/path/to/verified-knots/bin/bitcoind
export MININGCORE_TEST_POSTGRES='Host=127.0.0.1;Port=55432;Username=miningcore_test_admin;Password=LOCAL_TEST_ADMIN_PASSWORD;Database=miningcore_tests'
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj \
  --filter 'FullyQualifiedName~VarDiff|FullyQualifiedName~BitcoinBlake2b|FullyQualifiedName~BitcoinPublication'
```

Use an isolated regression node/database and a dedicated fixture administrator.
The production schema's migration guard correctly refuses a non-administrator runtime
role that owns the credit tables; do not grant the production runtime role migration
privileges to make a lab pass. The reviewed Knots release is
`29.4.2.knots20260508`; its Linux archive checksum is
SHA-256 `b59d0445a317e21a03dc29425db3aba79b27d5125230b1a2b1dce62e120827c5`, matching the
repository's CI pin. See the [BLAKE2b guide](bitcoin-blake2b.md) for daemon and
accounting lab contracts.

For a complete Linux suite, also enable the Bitcoin/Litecoin/Dogecoin and PostgreSQL
TLS fixture variables used by CI and set the native loader path:

```sh
export LD_LIBRARY_PATH="$PWD/src/Miningcore/bin/Debug/net10.0:$PWD/src/Miningcore.Tests/bin/Debug/net10.0"
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore
```

The full suite's legacy partition fixture explicitly switches to `miningcore`; reproduce
CI's bootstrap-role privileges inside the disposable PostgreSQL instance for that fixture.
Those privileges belong only to test infrastructure, never the original lab or production
runtime role.

## Validation evidence

Dated run results and environment details are recorded in
[PR #209](https://github.com/NINJAK1DD/miningcore/pull/209).
The commands above describe reproducible validation without depending on a particular
developer machine or local checkout path.

## Upgrade impact

No configuration or database migration is introduced by #185. VarDiff's runtime
timestamps are not persisted and are recreated when connections reconnect. Operators
may see more accurate adaptation for submillisecond shares and during UTC corrections.
Custom code using the old runtime timing fields must use the renamed integer counter
fields and the context's provider. Timestamp setters and context replacement are internal
to the assembly; custom family code must preserve the counter-domain contract rather than
write UTC/Unix values. Custom assignment hooks must acquire the shared worker gate and
hold it through publication; gated core hooks must not reacquire it across `await`.
Recursive or cross-worker nested use now throws `InvalidOperationException` instead of hanging. Prefer
`RunAssignmentAsync`; internal manual acquisitions activate the returned lease inside
the owner's `try`, then release it in `finally`. Subscription and NiceHash overrides now
accept the request `CancellationToken`; `OnSubscribeCoreAsync` requires a
`PreparedSubscription` value obtained before acquiring the gate. Downstream overrides
must update these signatures and preserve the preparation/commit boundary.
The existing positive `minDiff`/timing validation requirements from #184 still apply.
UTC-based request-age, job timestamp, activity and payout policies are outside this
elapsed-time change. Live regtest evidence does not establish physical-miner firmware
compatibility or long-running mainnet performance.

Two pre-existing protocol behaviors are tracked separately: bounded previous-difficulty
grace after static assignments in [#210](https://github.com/NINJAK1DD/miningcore/issues/210),
and coherent canonical Bitcoin minimum-difficulty notifications in
[#211](https://github.com/NINJAK1DD/miningcore/issues/211). Their acceptance criteria include
proof credit and live wire validation; this timing change retains their current behavior.
