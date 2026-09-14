# PostgreSQL command timeout policy

`persistence.postgres.commandTimeout` sets the Npgsql command timeout in **whole
seconds**. Miningcore accepts **1 through 86,400 inclusive**. Omission or `null`
keeps the existing **300-second** default. Explicit **zero is rejected** because
Npgsql interprets it as unlimited; Miningcore does not substitute a different
duration or provide an unlimited-query opt-in. Negative and oversized values
also fail startup, including `-rs` recovery startup.

```json
{
  "persistence": {
    "postgres": {
      "host": "db.example.net",
      "port": 5432,
      "database": "miningcore",
      "user": "miningcore",
      "sslMode": "VerifyFull",
      "tlsRootCert": "/etc/miningcore/postgres-root-ca.pem",
      "commandTimeout": 60
    }
  }
}
```

This is a configuration fragment; supply credentials through your existing
protected configuration/passfile and include the required pool settings.
The shipped main and TLS examples retain their explicit 60-second limit; the
300-second default applies only when the setting is omitted or `null`.
See [PostgreSQL TLS](postgres-tls.md) for certificate configuration.

## Why this policy

The five-minute default preserves existing bounded deployments and accommodates
financial transactions and recovery batches. Rejecting zero prevents an accidental
unlimited wait from retaining a transaction, locks or a connection indefinitely.
A warning alone would leave that behavior active. A separate unlimited opt-in
would perpetuate it in the same connection pool used for financial persistence.

The one-day maximum is an explicit Miningcore operational limit, not a PostgreSQL
or Npgsql maximum. It allows deliberate long recovery/maintenance commands while
remaining finite and comfortably below a signed 32-bit millisecond conversion
limit. It is not a recommended production setting. Choose the smallest practical
value after measuring workload duration and investigating slow queries and locks.
The default remains 300 seconds rather than changing every existing bounded
deployment to an unmeasured shorter timeout.

## What the timeout bounds

| Setting or mechanism | Units / value | Purpose |
| --- | --- | --- |
| Miningcore `commandTimeout` | Seconds; default 300 | Default timeout for commands on the persistence connection |
| Npgsql connection `Timeout` | Seconds; driver default 15, unchanged | Establishing a connection, distinct from executing SQL |
| Npgsql `Cancellation Timeout` | Milliseconds; driver default 2,000, unchanged | Additional wait for the response to a timed-out/cancelled query before breaking the connection |
| Caller cancellation token | Determined by the caller | Cancellation of an operation, independent of the configured command timeout |
| PostgreSQL `statement_timeout` / `lock_timeout` | Server settings; not changed here | Server-side statement/lock limits, configured separately |

The driver documents zero as infinite for command execution. Its connection and
cancellation settings have different meanings and units; this policy only changes
the accepted Miningcore command setting. See the [Npgsql parameter reference](https://www.npgsql.org/doc/connection-string-parameters.html#timeouts-and-keepalive)
and the [pinned 9.0.3 implementation](https://github.com/npgsql/npgsql/blob/v9.0.3/src/Npgsql/NpgsqlConnectionStringBuilder.cs).

This is **not a deadline for an entire transaction, recovery import, payout cycle,
or service shutdown**. Multiple commands, retries and cleanup add time. Existing
explicit command overrides, such as the payout lease's five-second commands,
remain in place. Driver I/O timeout behavior is not an end-to-end stopwatch for
streaming results. Server-side limits can terminate a statement sooner; review
them independently using the timeout documentation for
[PostgreSQL 18](https://www.postgresql.org/docs/18/runtime-config-client.html) or
[PostgreSQL 17](https://www.postgresql.org/docs/17/runtime-config-client.html).

Npgsql 9.0.3's binary COPY completion does not send a separate PostgreSQL cancel
request on timeout: it breaks the connection. The server may finish current work
before detecting the disconnect and rolling back. A client timeout therefore
does not prove that server work or locks have already stopped. This behavior is
visible in the [pinned COPY implementation](https://github.com/npgsql/npgsql/blob/v9.0.3/src/Npgsql/NpgsqlBinaryImporter.cs)
and is exercised by the live recovery test. The test waits for the backend
transaction to disappear before checking rollback and retry.

## Upgrade and maintenance

Before upgrading, inspect `persistence.postgres.commandTimeout`:

- Omitted, `null`, or integers from 1 to 86,400 retain their meaning.
- Replace explicit `0` with an intentionally chosen finite duration, or omit it
  to select 300 seconds. Zero previously meant unlimited; the new startup error
  is deliberate and names `persistence.postgres.commandTimeout`.
- Replace negative or greater-than-86,400 values. Strings, booleans, fractional
  values and objects are rejected before binding can coerce them or diagnostics
  can echo their contents. Supply a JSON integer token such as `100` or `300`.
  Floating-point spellings, including mathematically integral `1e2` and `300.0`,
  are deliberately rejected by the raw-JSON startup boundary.

Normal and recovery startup validate the same policy. The generated and bundled
schemas enforce the same numeric bounds (JSON Schema does not distinguish the
integral floating-point spellings rejected at startup), and the safe persistence diagnostic's
`CommandTimeout` reports exactly the duration passed to the driver. The full
connection string is never used as a diagnostic.

Before replacing the running service, use the **new release's binary** to check
your existing configuration, substituting its actual path:

```sh
dotnet Miningcore.dll -c /etc/miningcore/config.json --dumpconfig
```

Check the process exit status: a nonzero result means the pre-upgrade check failed.
This command parses, schema-validates and binds configuration, including the raw
PostgreSQL command-timeout policy, without starting mining, database or wallet
services or importing recovery shares. Its output is a credential-safe, lossy
diagnostic, not a configuration export; do not save it over the input.
Success confirms these read-stage checks only. Normal startup additionally runs
FluentValidation, deployment and cross-field checks and may still reject the
configuration. This command is not a full startup dry run or a connectivity test.
Perform this check before an incident: a legacy `commandTimeout: 0` also blocks
the new binary's emergency `-rs` recovery path until corrected.

For long maintenance, prefer a separate administrative session with its own
reviewed finite server-side limit. This Miningcore option does not configure
`psql`, schema migration tools or backup programs. For a large `-rs` import,
measure command duration, resolve blocking/index problems, and, if necessary,
temporarily increase the finite value in the recovery configuration. Restore the
normal value before restarting mining. Raising this setting also affects other
commands using that configuration; it does not extend unrelated shutdown or
recovery-drain budgets. See [database maintenance and recovery](database.md).

## Financial failure handling

The existing transaction and idempotency boundaries remain authoritative:

- A command failure before commit must leave no partial payment batch, payment
  history, balance debit, PPS receipt, credit or rounding remainder. Database
  retries use the existing payment/accounting identifiers to avoid duplicates.
- A recovery import timeout retains the journal and pending import evidence.
  Once its transaction has rolled back, retry can register and import it. If an
  earlier attempt committed but archival failed, its database manifest identifies
  the committed import so archival can resume without inserting the shares again.
- A timeout during COMMIT or after a wallet submission is not evidence that the
  financial operation failed. Preserve existing uncertain-outcome handling and
  reconcile database, journal and wallet evidence. Do not resubmit a wallet
  payment merely because its database persistence timed out, and do not erase
  retained recovery files/markers to force a retry.

The rollback contract is documented by [PostgreSQL](https://www.postgresql.org/docs/18/sql-rollback.html)
and [Npgsql transactions](https://www.npgsql.org/doc/basic-usage.html#transactions).
This change does not claim to resolve unknown commit outcomes or impose a global
transaction deadline.

## Reproduce the live checks

The [documented Windows/WSL lab](merged-mining-regtest-validation.md) has PostgreSQL
17 binaries on Windows; the primary Linux CI job installs PostgreSQL 18. The TLS,
timeout and payout persistence tests share one `IsolatedPostgresServer` collection
fixture: a temporary cluster, generated certificates, random loopback port and
synthetic financial data. Tests restore mutable authentication settings; timeout
and payout tests select both the valid certificate and trust authentication before
using their own schema and pool. Re-selecting an already active state avoids a
server restart. Existing lab databases, services and wallets are not used.

```powershell
dotnet restore src/Miningcore.Tests/Miningcore.Tests.csproj
dotnet build src/Miningcore.Tests/Miningcore.Tests.csproj --no-restore -p:BuildOdoCryptWindows=false
$env:MININGCORE_TEST_POSTGRES_BIN = 'C:/Program Files/PostgreSQL/17/bin'
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PostgresCommandTimeout' --logger 'trx;LogFileName=postgres-command-timeout.trx'
```

On Linux use an unprivileged account and point `MININGCORE_TEST_POSTGRES_BIN` at
the directory containing `initdb`, `pg_ctl` and `postgres`. The primary CI job
already installs and opts into this fixture; without that variable these live
tests are explicitly skipped. The Windows managed-only build above is sufficient
for these database tests and is not a production publish.
With the environment variable set, filtering to a unit test in the same policy
collection also initializes the shared server. Unset it for unit-only runs that
should avoid PostgreSQL setup.

The eleven database cases in `PostgresCommandTimeoutIntegrationTests` exercise
real command timeout/caller cancellation, transaction rollback, the one-slot
connection pool after failure, PPS replay and rounding, the payout handler's
database retry, COPY recovery, and interruption
between recovery commit and archival. A deferred-trigger COMMIT timeout also
verifies uncertain-outcome classification and database reconciliation before
idempotent persistence retry. Two cases exercise the actual payout handler's
default COMMIT error path, one for each persistence overload, and assert a single
wallet submission, payment batch, payment and debit after retry. A fixture
regression checks restoration from SCRAM authentication and disabled TLS.
Ordinary write/commit delays remain five seconds; reducing the uncancellable
COPY delay avoids waiting out a longer server sleep.

Financial assertions, trigger removal and pool-reuse checks wait for server
transaction completion. Assertions inspect database state before cleanup;
per-test teardown terminates only its generated backend identity, drops its schema
and clears its pool. Collection teardown stops/removes the temporary server.

Four additional [payout retry-contention cases](payout-persistence-tests.md) live
in `Payments/PayoutHandlerPersistenceIntegrationTests.cs`, alongside the payout
handler unit tests. They exercise the existing production retry policy across
one or two retries behind an unresolved COMMIT. The timeout-only filter above
does not select them; the payout guide gives both focused and combined commands.

### Multiple-retry verification: 2026-09-14

At `6534c09a`, before the payout-test relocation, the Windows lab (PostgreSQL
**17.10**, Npgsql **9.0.3**) passed **28 targeted tests**,
including all **fifteen live database cases**, cleanup and collection-isolation
checks. Both payout persistence overloads observed one and two distinct retry
transactions blocked by the original COMMIT, with one wallet submission and one
durable payment after release. Cancellation still interrupted the 15-second gate
sleep rather than waiting it out. The observer deadline is derived from that window
plus eight seconds, with another eight seconds for the second retry. The managed
build had zero warnings/errors; documentation links and diff-whitespace checks
passed. Local validation artifact (not committed):
`src/Miningcore.Tests/TestResults/issue147-multiple-retries.trx`.

### Broader verification record: 2026-09-13

Production code is unchanged since `d6dbc523`; subsequent commits change only
tests and documentation, so the earlier production-policy evidence still applies.

The Windows lab ran PostgreSQL **17.10**, Npgsql **9.0.3**, and .NET SDK **10.0.303**.
The managed build completed with zero warnings/errors. The configuration, TLS,
transaction, payout and share-recovery regression selection passed **769 tests**,
including all **thirteen live database cases**. One existing Unix permissions test was
skipped on Windows. Schema parity, diagnostic snapshot, documentation links and
diff-whitespace checks passed. Local test results (not committed) are recorded in
`src/Miningcore.Tests/TestResults/issue147-rereview.trx`; the diagnostic theory's
final isolated recheck is in `issue147-rereview-diagnostics.trx` in the same directory.

At revision `d6dbc523`, the [primary Linux CI run](https://github.com/NINJAK1DD/miningcore/actions/runs/34783987303)
used PostgreSQL **18.6** and Npgsql **9.0.3**: **2,840 tests passed**, with one skip,
including all ten live timeout cases present at that revision. Its Ubuntu and
Windows matrix also passed. The [release checks](https://github.com/NINJAK1DD/miningcore/actions/runs/34783987257)
and [macOS/Alpine portability checks](https://github.com/NINJAK1DD/miningcore/actions/runs/34783987345)
passed. These results establish PostgreSQL 18 coverage for that revision;
the PR checks separately report CI results for subsequent review changes.
