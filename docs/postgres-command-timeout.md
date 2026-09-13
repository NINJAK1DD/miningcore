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
      "commandTimeout": 300
    }
  }
}
```

This is a configuration fragment; supply credentials through your existing
protected configuration/passfile and include the required pool settings.
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
them independently using the [PostgreSQL 17 timeout documentation](https://www.postgresql.org/docs/17/runtime-config-client.html).

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
  can echo their contents. Supply a JSON integer.

Normal and recovery startup validate the same policy. The generated and bundled
schemas enforce the same numeric bounds, and the safe persistence diagnostic's
`CommandTimeout` reports exactly the duration passed to the driver. The full
connection string is never used as a diagnostic.

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

The rollback contract is documented by [PostgreSQL](https://www.postgresql.org/docs/17/sql-rollback.html)
and [Npgsql transactions](https://www.npgsql.org/doc/basic-usage.html#transactions).
This change does not claim to resolve unknown commit outcomes or impose a global
transaction deadline.

## Reproduce the live checks

The [documented Windows/WSL lab](merged-mining-regtest-validation.md) has PostgreSQL
17 binaries on Windows. The tests reuse the isolated TLS server fixture: a
temporary cluster, generated certificates, random loopback port and synthetic
financial data. Existing lab databases, services and wallets are not used.

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

The eight database cases exercise real command timeout/caller cancellation,
transaction rollback, the one-slot connection pool after failure, PPS replay and
rounding, the payout handler's database retry, COPY recovery, and interruption
between recovery commit and archival. A deferred-trigger COMMIT timeout also
verifies uncertain-outcome classification and database reconciliation before
idempotent persistence retry. Assertions inspect database state before
cleanup; teardown terminates only this test's generated backend identity, drops
its schema, clears its pool, and stops/removes its temporary server.

### Verification record: 2026-09-13

The Windows lab ran PostgreSQL **17.10**, Npgsql **9.0.3**, and .NET SDK **10.0.303**.
All eight live cases passed, including real COPY disconnect rollback and COMMIT
uncertainty/reconciliation. The managed build completed with zero warnings/errors;
documentation link and diff-whitespace checks passed.

Across the configuration, TLS, transaction, payout and share-recovery regression
selection and its snapshot recheck, **693 distinct tests passed**. One existing
Unix file-permission test was skipped on Windows. The broad run saved
`src/Miningcore.Tests/TestResults/issue147-final.trx` (692 passes and the old
60-second example snapshot mismatch); the regenerated fixture changes only that
value to 300, and its passing recheck is saved in
`src/Miningcore.Tests/TestResults/issue147-snapshot-recheck.trx`. These are local
test artifacts. Linux CI is configured to execute the live fixture separately;
this record does not claim a local Linux run.
