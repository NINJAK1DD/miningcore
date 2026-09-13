# PostgreSQL TLS policy and migration

For authenticated remote database connections, use `VerifyFull`. Encryption alone
does not prove that the peer is your database server. These settings apply to
`persistence.postgres` in normal startup and share recovery.

```json
{
  "host": "database.example.org",
  "port": 5432,
  "database": "miningcore",
  "user": "miningcore",
  "password": "CHANGE_ME_POSTGRES_PASSWORD",
  "sslMode": "VerifyFull",
  "tlsRootCert": "/etc/miningcore/postgres-root-ca.crt",
  "commandTimeout": 60
}
```

This is a PostgreSQL configuration fragment. Replace the corresponding object
in a complete [example configuration](../examples/README.md). Set `host` to a
name covered by the server certificate's subject alternative names; an IP
address needs a matching IP SAN. Protect configuration and client private keys
with service-account-only permissions. Protect CA files from unauthorized edits
and make them readable by the Miningcore account.

## Modes and compatibility

| `sslMode` | Behavior |
| --- | --- |
| `Disable` | Plaintext only. Appropriate only when another reviewed boundary protects the connection. |
| `Allow` | Plaintext first; TLS if required by the server. No server certificate authentication. |
| `Prefer` | TLS when available; can fall back to plaintext. No server certificate authentication. |
| `Require` | Requires encryption; does **not** authenticate the server certificate in Npgsql 9. |
| `VerifyCA` | Requires TLS and a valid chain to a trusted CA, including certificate validity dates; does not match the hostname. |
| `VerifyFull` | Requires TLS, a valid trusted chain, and a matching hostname. Recommended for remote connections. |

Use these exact, case-sensitive names in JSON. Unknown names, numbers (including
numeric strings), comma-separated modes, and whitespace aliases are rejected.
`null` and omitted modes have the same legacy meaning. Typed callers also reject
undefined enum values.

| Configuration | Effective policy |
| --- | --- |
| Mode omitted/null, `tls` omitted/null/false | `Prefer`, preserving Npgsql 9's previous default |
| Mode omitted/null, `tls: true` | `Require`, whether `tlsNoValidate` is omitted, false or true |
| Explicit mode with either non-null legacy flag (even false) | Rejected; remove both legacy flags |
| `tlsNoValidate: true` without `tls: true` | Rejected |
| `tlsRootCert` with a non-verifying mode | Rejected |
| Nonempty client certificate/key/password with `Disable`, `Allow` or `Prefer` | Rejected; select a mode requiring TLS |

`tlsNoValidate: false` has never upgraded `Require` to certificate validation
with the bundled Npgsql 9.0.3. The deprecated driver `Trust Server Certificate`
option is no longer emitted; no custom certificate-validation bypass is added.
The connection policy never retries a verifying connection using a weaker mode.
Invalid combinations fail configuration validation before database-dependent
services or listeners start. Certificate loading and handshake validation occur
when a physical connection is opened; they also fail closed.

## CA and client certificate sources

For `VerifyCA` and `VerifyFull`, a non-null `tlsRootCert` takes precedence over
`PGSSLROOTCERT`. An explicit path, or a non-null environment path present when the
factory is constructed, is copied into the connection string. Later environment
changes do not replace a path already copied there.

If neither path exists at construction, the connection string leaves the root
unset. **At each subsequent physical TLS connection**, Npgsql may read
`PGSSLROOTCERT` again, then check its default PostgreSQL `root.crt` location
(`~/.postgresql/root.crt` on Unix or `%APPDATA%/postgresql/root.crt` on Windows),
then use the operating system trust store. An environment variable set later or
a default file created later can therefore change the trust source. This is
intentional driver fallback behavior; the selected verifying mode never weakens.

Copying a path does not freeze its file contents either. Replacing a CA file at
that path can change which certificates subsequent physical connections accept.
Protect the service environment, default PostgreSQL trust directory, configured
CA files and system trust store for the entire process lifetime. An interactive
shell may have different trust sources. Startup validation checks configuration;
it does not snapshot trust-store state or reserve a CA file for future opens.

Use a PEM CA certificate file supported by Npgsql 9. An explicit missing,
unreadable or invalid file does not fall back to the environment or system store.
An empty/whitespace-only explicit or construction-time environment CA path is rejected.
The new `tlsRootCert` path is preserved exactly, including meaningful spaces.
In non-verifying modes `PGSSLROOTCERT` does not upgrade security; its value is
ignored, preserving legacy behavior. `PGSSLMODE` does not override the explicit
effective mode built by Miningcore.

`tlsCert`, `tlsKey` and `tlsPassword` remain client-authentication settings,
independent of server authentication. If omitted, Npgsql's existing `PGSSLCERT`,
`PGSSLKEY` and default client certificate locations still apply. PEM and PFX
support follows the bundled driver. Legacy client certificate/key paths retain
their previous surrounding-whitespace trimming; a nonempty whitespace-only path
now fails validation. Omitted, null and empty client paths retain driver fallback.
Passwords are never trimmed.
Npgsql's default certificate revocation checks remain disabled. This change does
not add CRL/OCSP controls or promise revocation enforcement.

For database password authentication, a nonempty `password` is literal and takes
precedence over driver credential sources. Omitted, null or empty passwords use
Npgsql's `PGPASSWORD`, then `PGPASSFILE` or the default PostgreSQL passfile. This
preserves the old connection-string parser's handling of `Password=;`; empty
does not disable fallback. Protect these sources under the service account and
test them with the server's required authentication method. TLS verification is
independent of password authentication.

## Upgrade checklist

1. Enable PostgreSQL TLS with a valid server certificate and private key; configure
   `pg_hba.conf` and firewall access for the Miningcore service account and host.
2. Deploy the trusted CA independently of the database connection. Check its
   provenance, expiry, permissions, and the server certificate's SANs.
3. Remove `tls` and `tlsNoValidate`; add `sslMode: "VerifyFull"` and, for a private
   CA, `tlsRootCert`. Existing valid legacy configurations retain their mode.
4. Remove stale client TLS settings previously ignored with `tls: false`. They
   now fail validation. Correct whitespace-only `tlsCert`/`tlsKey` paths or remove
   those fields. Do not add connection-string quoting inside JSON values:
   database, user, password and host are passed as literal data. Semicolons,
   equals signs, quotes, and surrounding whitespace cannot inject options.
   Database/user/password/host whitespace is preserved, rather than being
   accidentally normalized by the old concatenated string parser.
5. Test with the deployed service account, then restart Miningcore. Verify the
   allowlisted startup diagnostic reports `VerifyFull`. Confirm PostgreSQL
   reports TLS for the session; TLS alone is not proof of hostname validation.
   Exercise a deliberately wrong CA and wrong hostname in an isolated environment.
6. Fix trust/name/expiry failures at their source. Do not weaken the mode to restore
   connectivity. Restart Miningcore after trust-policy changes so pooled sessions
   cannot outlive the intended migration.

Startup connection diagnostics contain only port, effective SSL mode, timeout,
the legacy bypass flag and booleans indicating configured credentials/paths.
`RootCertificatePathConfigured` means a root path was encoded in Miningcore's
connection string. False does **not** mean that Npgsql uses no CA file: environment
and default-file fallback may still apply at physical connection time.
`PasswordConfigured` likewise describes a nonempty configuration value, not the
presence of a password obtained from an environment variable or passfile.
When a verifying mode has no encoded root path, startup also emits a fixed warning
that trust depends on the service account's ambient sources. This can be intentional
when using system trust; check the actual service environment, rather than assuming
that a CA variable set in an interactive shell reaches the daemon. The warning
contains no endpoint, credential or certificate-path values.
Host/database/user strings, password values and certificate/key paths are omitted.
Compared with PR #142, raw connection-string equality is deliberately replaced by
parsed field assertions: Npgsql canonicalizes keyword spelling, order and quoting.
The logging contract remains an explicit allowlist, now also excluding endpoint
and user strings and adding only root-certificate presence. Never log the builder
or its `ConnectionString`.

## Connection troubleshooting

Connection-open failures include a fixed category in the message and `Category`
property. They omit the driver's text, type name and exception graph, which can
contain certificate paths or server-supplied identity strings. Categories come
from structured exception types and a fixed set of SQLSTATEs, never
from parsing error messages. Inspect the service's `persistence.postgres`
configuration privately to identify its endpoint and credentials.

| Category | Checks under the deployed service account |
| --- | --- |
| `Network` | A socket error establishes network provenance. Resolve the configured host; check the configured port, listener, routing and firewall. |
| `Timeout` | Check reachability, server load, connection limits and timeouts. |
| `Authentication` | Verify the role, password source and `pg_hba.conf`; restricted server logs can help with database authentication rejection. |
| `LocalFileAccess` | Verify existence and access permissions for CA/client-certificate/key files **and PostgreSQL passfiles**, including `PGPASSFILE` and the default passfile. This category does not identify which file failed or imply a TLS problem. |
| `SecurityMaterial` | Check certificate/key encoding, key passwords and other cryptographic material. A cryptographic failure alone does not identify a particular file or prove a filesystem problem. |
| `TlsHandshake` | Check the trust chain, certificate validity dates, system clock and hostname/SAN match on the client. The provider does not expose reliable typed distinctions among those causes. |
| `DatabaseNotFound` | The server returned `3D000`: check the configured database name and provisioning. |
| `ConnectionLimit` | The server returned `53300`: check connection usage, pool sizes and server limits. |
| `ServerUnavailable` | The server returned `57P03`: check startup/recovery state and server availability. |
| `Configuration` | Check supported connection options and their types/ranges. |
| `Other` | Check SSL mode and whether the server supports TLS, credential availability, and server health; the provider supplied no safely classifiable cause. |

An `IOException` without a more specific structured cause remains `Other`: both
local files and transport streams can produce it. Definite file-access errors
retain `LocalFileAccess` even inside handshake wrappers. A generic cryptographic
error inside an authentication exception remains `TlsHandshake` because it may
come from certificate verification.

The `Other` message includes a conditional reminder to check server TLS support.
Selecting `Require`/`VerifyCA`/`VerifyFull` does not establish that an unknown error
means TLS is unavailable; the classifier never makes that inference or parses
provider messages to invent a more specific diagnosis.

Client-side certificate rejection may appear only as a reset in PostgreSQL logs;
those logs cannot explain all trust or hostname failures. Verify the deployed CA
and server certificate privately and retain `VerifyFull` while correcting them.
No sensitive trace-logging escape hatch is enabled.

The replacement remains an `NpgsqlException` and preserves the original driver's
`IsTransient` for existing retry policies. That flag is not proof that waiting
will fix the cause: Npgsql classifies a missing CA file's nested I/O error as
transient. Cancellation remains cancellation even if failed-open disposal also
fails. Cleanup errors cannot escape this diagnostic boundary. SQL execution
errors after opening remain outside this connection-diagnostic boundary.

## Reproducible real TLS tests

The documented Windows/Ubuntu WSL test lab has PostgreSQL 17 on Windows. These
tests use its binaries to create a separate ephemeral cluster, synthetic CAs and
server certificates, a random loopback port, and no mining/payout data. Existing
lab databases and services are not modified. The test cluster normally uses local
trust authentication to isolate TLS behavior; dedicated password tests switch only
that cluster to SCRAM-SHA-256. Run it on a trusted test machine.

```powershell
dotnet build src/Miningcore.Tests/Miningcore.Tests.csproj --no-restore -p:BuildOdoCryptWindows=false
$env:MININGCORE_TEST_POSTGRES_BIN = 'C:/Program Files/PostgreSQL/17/bin'
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PostgresTlsIntegrationTests' --logger 'trx;LogFileName=postgres-tls.trx'
```

The managed-only build is sufficient for database tests; it is not a production
publish. On Linux use an unprivileged account with PostgreSQL binaries installed
and set `MININGCORE_TEST_POSTGRES_BIN` to their directory. Without this explicit
opt-in the live test is marked skipped. Every matrix entry opens a fresh physical
connection with pooling disabled and verifies either `pg_stat_ssl` or the expected
TLS failure category. Cases cover trusted/untrusted CA, expiration, hostname
mismatch, `VerifyCA` versus `VerifyFull`, missing/invalid CA files, environment CA
precedence, changes after factory construction, file-content replacement and
unavailable TLS. Separately reported theory cases share an isolated class fixture;
each case explicitly selects its certificate mode. Other tests cover SCRAM
password sources, missing/unreadable passfiles, database-selection errors, safe
error categories, cancellation and an occupied-port retry. The unreadable-passfile
case uses Unix permissions and is explicitly skipped on Windows. SCRAM test
cleanup attempts every restoration and preserves the original failure alongside
any teardown failures.
Port selection occurs immediately before startup and retries at most three times
only when the port is occupied and the temporary server has exited.
The test stops and removes its own temporary cluster.

CI pins the TLS server major to PostgreSQL 18 on Ubuntu 26.04 and selects its
binary directory explicitly; distribution security patch updates remain enabled.
Set `MININGCORE_TEST_POSTGRES_BIN` to another installed major for compatibility
testing. Process-global environment changes run in an xUnit collection with
`DisableParallelization = true`; the pinned xUnit 2.4.2 runner executes those
collections one at a time after all parallel-capable collections finish.

References: [Npgsql TLS documentation](https://www.npgsql.org/doc/security.html#encryption-ssltls),
[Npgsql 9.0.3 TLS implementation](https://github.com/npgsql/npgsql/blob/v9.0.3/src/Npgsql/Internal/NpgsqlConnector.cs),
and [Npgsql connection parameters](https://www.npgsql.org/doc/connection-string-parameters.html).
