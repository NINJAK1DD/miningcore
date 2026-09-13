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
`PGSSLROOTCERT`. The effective environment path is resolved when the connection
factory is constructed and included as data in the connection string. With
neither configured, Npgsql uses its default PostgreSQL `root.crt` location when
present (`~/.postgresql/root.crt` on Unix or
`%APPDATA%/postgresql/root.crt` on Windows), otherwise the operating system trust
store. Audit the service account's environment and default certificate directory;
an interactive shell may have different trust sources.

Use a PEM CA certificate file supported by Npgsql 9. An explicit missing,
unreadable or invalid file does not fall back to the environment or system store.
An empty/whitespace-only explicit or effective environment CA path is rejected.
The new `tlsRootCert` path is preserved exactly, including meaningful spaces.
In non-verifying modes `PGSSLROOTCERT` does not upgrade security; its value is
ignored, preserving legacy behavior. `PGSSLMODE` does not override the explicit
effective mode built by Miningcore.

`tlsCert`, `tlsKey` and `tlsPassword` remain client-authentication settings,
independent of server authentication. If omitted, Npgsql's existing `PGSSLCERT`,
`PGSSLKEY` and default client certificate locations still apply. PEM and PFX
support follows the bundled driver. Legacy client certificate/key paths retain
their previous surrounding-whitespace trimming; passwords are never trimmed.
Npgsql's default certificate revocation checks remain disabled. This change does
not add CRL/OCSP controls or promise revocation enforcement.

## Upgrade checklist

1. Enable PostgreSQL TLS with a valid server certificate and private key; configure
   `pg_hba.conf` and firewall access for the Miningcore service account and host.
2. Deploy the trusted CA independently of the database connection. Check its
   provenance, expiry, permissions, and the server certificate's SANs.
3. Remove `tls` and `tlsNoValidate`; add `sslMode: "VerifyFull"` and, for a private
   CA, `tlsRootCert`. Existing valid legacy configurations retain their mode.
4. Remove stale client TLS settings previously ignored with `tls: false`. They
   now fail validation. Do not add connection-string quoting inside JSON values:
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
Host/database/user strings, password values and certificate/key paths are omitted.
Compared with PR #142, raw connection-string equality is deliberately replaced by
parsed field assertions: Npgsql canonicalizes keyword spelling, order and quoting.
The logging contract remains an explicit allowlist, now also excluding endpoint
and user strings and adding only root-certificate presence. Never log the builder
or its `ConnectionString`.

Connection-open failures also omit the driver's exception graph, which can
contain certificate paths or server-supplied identity strings. The replacement
remains an `NpgsqlException` and preserves `IsTransient` for retry decisions;
cancellation remains cancellation. Use restricted PostgreSQL server logs to
diagnose authentication failures. SQL execution errors after opening are outside
this connection-diagnostic boundary.

## Reproducible real TLS tests

The documented Windows/Ubuntu WSL test lab has PostgreSQL 17 on Windows. These
tests use its binaries to create a separate ephemeral cluster, synthetic CAs and
server certificates, a random loopback port, and no mining/payout data. Existing
lab databases and services are not modified. The test cluster uses local trust
authentication solely to isolate TLS behavior; run it on a trusted test machine.

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
precedence and unavailable TLS. The test stops and removes its own temporary cluster.

References: [Npgsql TLS documentation](https://www.npgsql.org/doc/security.html#encryption-ssltls),
[Npgsql 9.0.3 TLS implementation](https://github.com/npgsql/npgsql/blob/v9.0.3/src/Npgsql/Internal/NpgsqlConnector.cs),
and [Npgsql connection parameters](https://www.npgsql.org/doc/connection-string-parameters.html).

### Verification record: 2026-09-13

The documented Windows lab's PostgreSQL 17.10 binaries and bundled Npgsql 9.0.3
passed the 20-case physical-connection matrix, plus factory opening, safe error
and cancellation checks. The selected configuration, recovery, pool-template,
diagnostic and TLS suite passed 551 tests; one Linux-only socket-binding test was
skipped on Windows. The managed build completed with zero warnings/errors.
Results were saved to `src/Miningcore.Tests/TestResults/issue146-final.trx`.
The primary CI job now installs PostgreSQL binaries and opts into the isolated
TLS fixture; that Linux CI run is not represented as executed by this local record.

PR preparation replayed the change on `dev` at `81603aa`, including the merged
PR #142 logging contracts and current diagnostic projection. The updated
configuration suite passed 781 tests with one Linux-only socket-binding test
skipped; the real PostgreSQL TLS matrix passed in that checkout as well.
The reviewed diagnostic fixture adds only `sslMode` and CA-path presence.
The clean managed test build used `BuildOdoCryptWindows=false` and
`DisableGitVersionTask=true` because the PR checkout was shallow; neither is a
production publishing setting. Results are in `issue146-pr-configuration.trx`
and `issue146-pr.trx` under the test project's `TestResults` directory.
