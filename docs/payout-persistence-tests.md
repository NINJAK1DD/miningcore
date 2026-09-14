# Payout persistence retry tests

`src/Miningcore.Tests/Payments/PayoutHandlerPersistenceIntegrationTests.cs` owns
the four live PostgreSQL retry-contention cases for `PayoutHandlerBase`, alongside
its unit tests. These cases exercise existing payout idempotency under lock
contention; they do not change the [command-timeout policy](postgres-command-timeout.md).

For new coverage, live PostgreSQL tests whose subject is payout behavior belong
under `Payments/`; tests whose subject is the persistence layer belong under
`Persistence/Postgres/`. Existing suites outside this placement rule are not
relocated by this change.

For each persistence overload, the test observes one or two distinct retry
transactions blocked by an unfinished COMMIT. The production retry hook,
exponential backoff and Npgsql cancellation budget remain unchanged. A test-only
deferred trigger absorbs the first cancellation and waits on an observer-owned
advisory lock. The observer releases that gate only after PostgreSQL reports the
required number of retry transactions blocked by the original payment-batch key.
In the two-retry cases, the first retry times out naturally before the second
reaches that same unresolved transaction. Backend PID and transaction start time
distinguish attempts, including pooled backend reuse and repeated polling.

After release and idempotent replay, each case asserts one wallet submission,
one payment batch, one payment and one debit. The advisory gate is released and
the payout awaited even when overlap observation fails. Early completion reports
task status; deadline failures report observed versus required attempts and the
observer-poll/cancellation/backend/retry-budget assumptions to check. Financial
assertions and pool-reuse checks wait for server transaction completion.

## Timing assumptions

The trigger's **15-second cancellation window** must survive until `query_canceled`
arrives and arms the advisory gate, including scheduling jitter. Cancellation
normally interrupts the sleep after roughly one second. Gate release follows
observed blocking state, independent of the driver's SQL spelling; see
[PostgreSQL advisory locks](https://www.postgresql.org/docs/18/explicit-locking.html#ADVISORY-LOCKS).

The one-retry observation budget is the gate window plus eight seconds
(**23 seconds**). This lets an unarmed gate finish its sleep and expose the payout
outcome before the deadline. It also covers the one-second command timeout,
two-second cancellation budget, two-second first backoff and reconnect/observation.
The second retry adds eight seconds (**31 seconds** total), covering another
one-second command timeout, up to two seconds of cancellation, the four-second
second backoff and reconnect slack.

This linear addition is valid only because `Assert.InRange` caps the required
retry count at **two**. Production backoff doubles (2, 4, 8, 16 seconds, etc.);
extending the cases requires revisiting both the cap and the budget. Changes to
the retry policy, Npgsql cancellation semantics or PostgreSQL wait-event names
require checking these assumptions. The existing four cases establish the
invariant without exercising all eight production retries.

Each failed retry is visible to the observer only during its roughly one-second
command-timeout window. The 25 ms polling delay is added to a database round trip;
it does not guarantee a sample within that window. A slow query or scheduling
stall can miss an entire retry, especially on a loaded runner. A longer deadline
cannot recover a missed observation, although a later retry may still be observed.
Failures therefore name observer-poll starvation alongside the other timing
assumptions. Observer continuations avoid the caller's synchronization context
to avoid introducing additional scheduling delays.

## Run the live tests

Use the [documented isolated Windows/WSL lab](merged-mining-regtest-validation.md)
and [managed build instructions](postgres-command-timeout.md#reproduce-the-live-checks).
The payout and timeout suites share `PostgresPersistenceTestDatabase`,
`PayoutPersistenceTestHandler` and the `IsolatedPostgresFact`/`IsolatedPostgresTheory`
opt-in attributes under `src/Miningcore.Tests/Util`. The shared server, collection
definition and cleanup helper also live there, and the database helper builds its
own connection configuration without depending on a test class. Neither suite
owns the other's shared helpers. Timeout-only fault injection stays in the timeout
suite.
The TLS suite also uses these internal attributes; its Unix-permission case keeps
the additional platform restriction in `PostgresTlsUnixFact`.
An inventory test requires every test method in all three suites to use the
isolated opt-in attributes (or the TLS Unix specialization). These attributes
require `MININGCORE_TEST_POSTGRES_BIN` to name the PostgreSQL **bin directory**;
the separate `PostgresIntegrationFact` instead uses the `MININGCORE_TEST_POSTGRES`
connection string for an existing service database.
`SchemaAndApplicationName` explicitly identifies the generated schema, search path
and application name shared by each case's connections. Its
`miningcore_persist_{guid}` prefix identifies test-owned schemas; even the
longest identity, with the observer suffix, fits PostgreSQL's 63-byte limit.
The shared payout adapter accepts a transaction ID, defaulting to `tx-1`; the
contention cases exercise distinct batch and recipient IDs through replay.
The suites use the same collection-owned `IsolatedPostgresServer` as TLS tests,
retaining process-state isolation and one temporary server. Each case owns its
schema, application identity and one-slot pool. Existing lab databases and wallets
are not used.

```powershell
$env:MININGCORE_TEST_POSTGRES_BIN = 'C:/Program Files/PostgreSQL/17/bin'
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PayoutHandlerPersistenceIntegrationTests' --logger 'trx;LogFileName=payout-persistence.trx'
```

To run all fifteen timeout/payout database cases plus cleanup and collection
isolation checks, use this combined filter:

```powershell
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PostgresCommandTimeoutIntegrationTests|FullyQualifiedName~PayoutHandlerPersistenceIntegrationTests|FullyQualifiedName~PostgresTestCleanupTests|FullyQualifiedName~NonParallelCollectionTests' --logger 'trx;LogFileName=payout-persistence-relocation.trx'
```

To check the shared attributes' TLS consumers too, append
`|FullyQualifiedName~PostgresTlsIntegrationTests` to that filter. The Unix-permission
case intentionally skips on Windows.

On Linux, point `MININGCORE_TEST_POSTGRES_BIN` at the PostgreSQL binary directory
and run as an unprivileged user. Primary Linux CI already installs PostgreSQL 18
and enables these cases. Without the environment variable the live tests skip.

The primary Linux workflow already runs the complete timeout and payout suite:
it exports `MININGCORE_TEST_POSTGRES_BIN=/usr/lib/postgresql/18/bin` before the
unfiltered `dotnet test` step. The [Linux run at `f2afb229`](https://github.com/NINJAK1DD/miningcore/actions/runs/34815000311)
records all **15 cases passing** with PostgreSQL **18.6** and Npgsql **9.0.3**,
including each of the four payout overlap cases. This is CI execution evidence,
separate from the local Windows artifacts below.

## Relocation verification: 2026-09-14

The documented Windows lab (PostgreSQL **17.10**, Npgsql **9.0.3**) passed all
**28 combined checks**, including **15 live database cases**, with zero failures
or skips after relocating the four contention cases. The managed build completed
with zero warnings/errors; documentation links and diff-whitespace checks passed.
Local validation artifact (not committed):
`src/Miningcore.Tests/TestResults/payout-persistence-relocation.trx`.
The `TestResults` directory is gitignored; these paths identify local evidence,
not files available in a repository checkout or uploaded CI artifacts.
Earlier policy evidence remains in the [timeout verification record](postgres-command-timeout.md#broader-verification-record-2026-09-13).

The review follow-up passed the same **28 checks**, including all **15 live cases**,
after moving the shared helpers to `Util` and extracting the generic opt-in
attributes. Without the opt-in variable, **13 checks passed** and **eight test
methods skipped**; xUnit reports skipped theories once without expanding their
rows. Both runs had zero failures, and the managed build had zero warnings/errors.
Local validation artifacts (not committed):
`src/Miningcore.Tests/TestResults/payout-review-live.trx` and
`src/Miningcore.Tests/TestResults/payout-review-opt-out.trx`.

The TLS attribute follow-up passed **55 checks** with zero failures and the one
expected Unix-permission skip on Windows, including all 15 timeout/payout cases.
Without the opt-in variable, **13 checks passed** and **17 methods skipped**.
The build had zero warnings/errors. Local artifacts (not committed):
`src/Miningcore.Tests/TestResults/postgres-shared-attributes-live.trx` and
`src/Miningcore.Tests/TestResults/postgres-shared-attributes-opt-out.trx`.

The fixture-ownership and discovery-guard follow-up passed **56 checks** with
zero failures and the expected Unix-permission skip on Windows. This includes
all **15 timeout/payout live cases** and the new opt-in inventory check. Without
the opt-in variable, **14 checks passed** and **17 methods skipped**. The managed
build had zero warnings/errors. A temporary mutation replacing an isolated fact
with plain `[Fact]` made the inventory check fail with the offending method's
name; the annotation was restored before the final build and both passing runs.
Local artifacts (not committed):
`src/Miningcore.Tests/TestResults/isolated-postgres-review-live.trx`,
`src/Miningcore.Tests/TestResults/isolated-postgres-review-opt-out.trx` and
`src/Miningcore.Tests/TestResults/isolated-postgres-guard-mutation.trx` (the expected
failure proving the guard). Current-head Linux evidence, including the Unix-only
TLS case, is recorded in the PR checks and description.
