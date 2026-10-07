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
inactivity eligibility and the full monotonic retarget cooldown; the existing one-second
scheduler margin cannot shorten that cooldown. No-op idle sweeps preserve the next
real share's baseline and buffer. Actual retargets clear the buffer and restart the
cooldown. A broken counter moving backward or poisoned interval history is defensively
rebased without changing the last assignment marker.

An idle producer skips a contended monitor. Share producers recheck cancellation and
context identity after acquiring the monitor, so a disabled/replaced context observed
while waiting cannot supply a stale calculation. `PoolBase` checks cancellation before
the operation and forwards its token to both producers. BLAKE2b's existing assignment
gate still serializes enabled-state checks, calculation and publication with fixed
assignments. Its terminal failure and shutdown boundaries remain in place; the
migration introduces no new assignment/publication protocol for other families.

Positive-interval proportional arithmetic, overflow/underflow recovery, configured
minimum/maximum and `maxDelta` remain intact. Unresolved zero windows retain the
conservative **0.001 / min(interval count, 10)** second policy estimate from #184,
including the current interval. This is now an explicit policy floor rather than a
claim about the monotonic provider's resolution. Sparse zero windows cannot claim the
same rate as full windows; tiny target intervals do not cause a zero estimate to lower
difficulty. BLAKE2b still supplies its representable maximum without rewriting config.

## Regression and live validation

`VarDiffMonotonicTests` compare identical share/idle counter sequences across all
concrete worker contexts while moving UTC forward and backward. They cover creation,
first share, idle before first share, strict cooldown, resets, replacement/disabled
contexts, canceled operations, real monitor contention and provider-specific units.
`VarDiffManagerTests` retain arithmetic, zero-window, poisoned-history, no-op sweep and
configured-bound coverage, and explicitly measure positive submillisecond intervals.

The BLAKE2b wire tests cover both wall-step directions, retained jobs, representable
new work, disable-before-calculation and assignment-before-fixed-difficulty races.
Existing cancellation and publication-failure cases use deterministic monotonic
contexts. Canonical Bitcoin's publication and accepted-credit fixtures are migrated too.

The daemon-backed `BitcoinBlake2bRegtestTests` use real header-v2 proofs from the pinned
Knots node and PostgreSQL accounting. New forward/backward UTC correction cases hold
the monotonic interval at five seconds and verify the same retarget, matching target
notification, original proof difficulty, usable connection and exactly-once settlement
for SOLO, PPS, PROP and PPLNS. Existing zero-window, delta-limit and terminal-publication
cases remain enabled. These tests require both environment variables; skipped daemon
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

## Validation on 7 October 2026

Validation used the documented Ubuntu 26.04 WSL lab with .NET SDK 10.0.112,
PostgreSQL 18.6 and the checksum-verified Knots pin above. The isolated checkout was
`/home/ubuntu/issue-185/miningcore`; all 926 C# source files were byte-identical to the
Windows fix checkout. The native build compiled all 25 components and passed the
repository warning audit with zero warnings and zero errors.

The focused Linux filter above passed **695 tests, zero failures, zero skips**.
Its TRX explicitly records both new forward/backward live-proof cases as passed.
The Windows managed regression filter passed **676 tests, zero failures**, with
19 daemon/ledger cases explicitly skipped there and enabled in Linux. Repository-local
documentation links and heading anchors passed verification.

The complete Linux suite passed **3,875 tests, zero failures**, with only the opt-in
`Run_Benchmarks` test skipped (3,876 total). Both new live-proof correction cases also
passed in this full run. The full suite used CI's native loader path and bootstrap-role
privileges inside the disposable database; two initial environment-only failures
(native library discovery and the legacy partition fixture's `SET ROLE`) were corrected
before this clean rerun. The focused live run used the dedicated fixture administrator.

The lab's existing non-administrator `miningcore` role correctly failed the production
migration guard during initial fixture setup. The successful live run used a separate
temporary PostgreSQL instance on loopback port 55485, with its own fixture administrator
and a private parent directory. The original lab role/database and migration guard
were left intact. The broader lab suite also exercised real Bitcoin Core 31.1,
Litecoin 0.21.5.8 and Dogecoin 1.14.9 binaries. Raw build logs and TRX results are kept
under `build/issue-185` in the fix checkout and isolated Linux checkout.

## Upgrade impact

No configuration or database migration is introduced by #185. VarDiff's runtime
timestamps are not persisted and are recreated when connections reconnect. Operators
may see more accurate adaptation for submillisecond shares and during UTC corrections.
Custom code using the old runtime timing fields must use the renamed integer counter
fields and the context's provider; UTC/Unix timestamps cannot be assigned to them.
The existing positive `minDiff`/timing validation requirements from #184 still apply.
UTC-based request-age, job timestamp, activity and payout policies are outside this
elapsed-time change. Live regtest evidence does not establish physical-miner firmware
compatibility or long-running mainnet performance.
