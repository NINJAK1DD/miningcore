# Credential-safe Stratum diagnostics

This is the miner-input audit for [#157](https://github.com/NINJAK1DD/miningcore/issues/157).
It complements the [RPC consumer boundary](rpc-consumer-diagnostics.md); it is not
a global log-redaction facility or a change to Stratum validation.

## Threat model and design

A miner can place a password or forged log line in any JSON value, property name,
request ID, method, worker identity, user agent or PROXY-protocol header. Malformed
JSON can copy that input into exception messages and paths. An owned exception type
does not establish that its message is safe. Escaping arbitrary text stops literal
line injection but still discloses its contents.

The design follows the [OWASP Logging Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html)
guidance to exclude authentication passwords and treat untrusted event data as an
injection boundary. [JsonReaderException](https://www.newtonsoft.com/json/help/html/t_newtonsoft_json_jsonreaderexception.htm)
exposes parsing paths and messages, so neither is diagnostic metadata here.
Instead of attempting to recognize secrets, the affected output is constructed
from a closed vocabulary and typed numbers. No exception object is attached to the
log event: a custom NLog exception layout cannot recover it. This projection does
not invoke exception `Message`/`ToString`, serialize requests, or use ambient
Json.NET converters/settings.

The method projection also closes an unbounded telemetry-cardinality path. Previously,
distinct arbitrary request methods flowed into Prometheus label values and could grow
registry memory and scrape output indefinitely. The closed vocabulary bounds that
dimension; it is not a general request-rate or memory-budget defense. Debug records
still have per-event serialization cost. They return before constructing JSON when
disabled; the concrete JSON tree retains escaping and serializer-setting isolation.
Pooling/hand-written serialization is deferred until profiling demonstrates a need.

## Inventory and output boundaries

| Source / path | Untrusted fields previously reachable | Retained diagnostic output |
| --- | --- | --- |
| `StratumConnection.FillReceivePipeAsync`, `ProcessReceivePipeAsync` | Full buffered miner JSON, including incomplete requests and credentials | Server connection ID, received/buffered byte count; structured `ReceiveWait`/`BufferWait` events |
| `StratumConnection.SendMessage` | Serialized reply/notification, echoed IDs, arbitrary error text | Server connection ID and serialized byte count before the newline |
| `StratumConnection.ProcessProxyHeader` | Raw trusted-proxy header, including malformed addresses/ports | Header byte count; successfully parsed numeric client IP under existing logging settings |
| `StratumConnection.DispatchAsync` terminal callbacks | Raw callback/teardown exception and inner exception text | Fixed terminal-callback event, structural failure and numeric code |
| `StratumServer.OnRequestAsync` and Ethereum V1 warnings | Miner method and JSON-RPC request ID; telemetry method label | Finite method vocabulary; request ID omitted; unknown telemetry methods grouped as `other` |
| `StratumServer.OnConnectionError` | Parser message/path, socket/TLS/IO/argument/other exceptions | Connection ID, failure category and numeric code; existing fixed ban messages |
| Server listen, accept, tracked/untracked completion, task removal and drain observers | Exceptions propagated from callbacks/connection tasks | Fixed event and failure category, connection ID where owned; listener errors retain the port |
| Server certificate load | Exception text and configured certificate path | `CertificateLoad` event, port, structural category and bounded reason; no path or password |
| Coin-pool authorization and hello handlers | Miner/worker identity, arbitrary user agent, Kaspa validation error text | Authorized/unauthorized/hello outcome, connection ID and existing ban duration; fixed invalid-address reason |
| Xelis, Alephium, Kaspa job lookup | Miner identity and submitted job ID | Connection ID and `job-not-found`; original lookup/ASIC compatibility logic unchanged |
| Coin job-manager block-acceptance logs, including merged Bitcoin | Miner identity | Existing block height/hash with identity withheld |
| Beam address-validation warnings | Supplied wallet address | Existing wallet-kind warning and numeric offline-payment count, address withheld |
| `PoolBase` miner-effort check | Miner identity | Server connection ID and numeric effort |
| Coin share-rejection catches, pool/job observable callbacks | Original share/parser exception | Existing RPC-consumer projection, reviewed for indirect propagation; no raw message restored |

The coin-pool inventory covers Bitcoin (and inherited implementations), Equihash,
Handshake, Nexa, ProgPoW, Satoshicash, Ethereum, CryptoNote, Conceal, Zano, Alephium,
Beam, Ergo, Kaspa, Warthog and Xelis. The supporting source audit also inspected the
JSON-RPC model/parser, worker contexts, banning implementation and shared pool
callbacks. Static difficulty, share counters, ban percentages/durations and listener
ports remain typed numeric metadata, not copies of password-control strings.

## Transport diagnostic contract

New transport records start with `Stratum diagnostic ` followed by compact JSON:

`event` is always present. Optional fields are omitted when unavailable or inapplicable;
the projection does not emit explicit JSON nulls. This keeps read-cycle waiting events
to the event name and server connection ID, without a growing set of empty fields.

| Field | Meaning |
| --- | --- |
| `event` | Finite `StratumDiagnostics.Event` value; unknown enum values become `other`. |
| `connectionId` | Server-generated correlation ID, omitted before a connection exists or for aggregate drain failures. Never a JSON-RPC ID, worker/session ID or proxy field. |
| `failure` | Shared fixed structural category, omitted for non-failure events; authentication errors use `tls-handshake`, cryptographic errors use `cryptographic`. |
| `code` | Numeric native/protocol error code where available; absence does not mean success. Zero codes are retained. |
| `method` | Reviewed protocol method for `Request` events only. Unknown or missing request methods become `other`; the field is omitted for other events. |
| `bytes` | Received/buffered/serialized/header byte count where relevant, including zero; otherwise omitted. |
| `port` | Configured listener port for listener/certificate failures; otherwise omitted. |
| `reason` | Certificate-load reason: `file-not-found`, `access-denied`, `file-io`, `invalid-certificate-or-password`, or `other`; otherwise omitted. |

Socket error codes are platform-native; Windows and Linux numbers need not match.
Share errors retain the existing fixed `job-not-found`, `duplicate-share`,
`low-difficulty-share`, `unauthorized-worker` and related categories, with their own
numeric codes. Alephium's separate error-code mapping remains independent.
Connection correlation is supplied by `CorrelationIdGenerator`, not extracted from
miner input. Extensions must preserve that trust boundary and add new method labels
explicitly; they must not use arbitrary strings as diagnostic vocabulary.

Certificate reasons inspect exception types and at most four cryptographic wrappers,
not messages, paths or textual native errors. Invalid PFX data and a wrong password
may share a runtime error: the diagnostic deliberately does not invent a distinction.
Use the retained pool/listener port to locate its PFX setting in private configuration,
then check file existence/access and certificate/password correctness. Configured
filenames remain withheld: operator-controlled text can still contain secrets or
private filesystem details.

## Compatibility and limits

Earlier PR revisions emitted optional fields as explicit nulls. Consumers must accept
missing optional properties as unavailable, tolerate additive fields, and not require
a fixed property count. Conditional field additions use the concrete JSON tree, not
ambient serializer settings. The RPC consumer record retains its existing field shape;
its shared failure-category changes are covered in the
[RPC compatibility notes](rpc-consumer-diagnostics.md#compatibility).

Only diagnostic output and telemetry label projection change. The original request,
reply, exception, authorization result, share/counter updates, mining fail-stop gate,
socket ownership, cancellation and ban branches are not rewritten. In particular,
the legacy junk-ban distinction is retained: a missing `Banning` object does not
ban; an existing object with unset/true `BanOnJunkReceive` does; false disables it.
Oversized-input and malformed PROXY failures do not acquire a new junk-ban rule.

Unknown request methods now share the telemetry label `other`; request event timing
and counting remain unchanged. Update dashboards or parsers that matched raw method
names, worker strings, exception text or the old NET/PIPE payload dumps. Known method
labels remain available. Detailed raw causes require controlled private debugging,
not a second plaintext target or a switch that restores payload logging.

Worker names and user agents remain arbitrary miner input even after authorization;
they can contain the same password as any other request field. Neither Debug level
nor `Logging.GPDRCompliant=false` makes that text safe. These logs intentionally cannot
reconstruct a connection-to-worker-name mapping. For Bitcoin-family subscriptions,
correlate the server connection ID returned in the subscription reply with timestamps;
other identity investigations require authorized private runtime/accounting evidence.
Configured Ethash prefixes also remain behind the bounded projection, including in
V1 warning branches: matching a request to a configured prefix is not a confidentiality
check. Custom prefixes require explicit vocabulary review, not direct interpolation.

Existing fixed connection/lifecycle messages, numeric IP addresses, configured pool
identities, block metadata, difficulty, counts and timing remain operational metadata.
The existing IP-censor flag is now honored consistently by both early banned-IP and
already-connected banned-client messages. It remains partial address masking, not
an anonymity guarantee, and does not authorize raw identity or credential logging.
Connection initialization and acceptance logging also tolerate an absent `Logging`
object, treating censoring as not enabled, consistently with the banned-IP paths.
This removes an incidental null-reference connection rejection for that configuration;
explicitly configured privacy settings and ban decisions retain their meaning.
Databases, accepted-share/accounting records, payout records, API responses, periodic
worker statistics/notifications, wire replies, external proxies, plugins and arbitrary
application logs are not globally redacted by this change. Do not put credentials in
worker names or assume that identifiers are private everywhere. The real protocol
still transports credentials and may echo request IDs: use appropriately secured
transport. No payout-accounting or daemon TLS policy is changed.

Earlier Debug logs could contain complete Stratum requests and responses; parser
errors could expose request fragments at error level, and identity logs existed at
Info. Restrict access to retained logs and exported support bundles, investigate the
actual exposure, and rotate credentials that were disclosed. Upgrading does not
remove historical copies.

## Verification

`StratumDiagnosticTests` captures all log levels with an exception/properties layout
and inspects the original log events. It uses synthetic credentials only and real
loopback TCP sockets for hostile JSON, IDs/methods/parameters, authorization,
oversized input, PROXY headers, stale-job lookup and terminal-callback failures.
Malformed TLS handshakes run through the actual server TLS stream. It checks wire
replies, preserved input values, telemetry labels/counts, callback counts, and ban
addresses/durations. Structural exception cases include a type whose `Message` and
`ToString` throw; every transport event is tested against every failure fixture.
Known-method coverage is checked against all declared `*StratumMethods` constants
and the Ethash V1 method names composed from bundled coin prefixes (including
Cortex's four `ctxc_*` methods). Runtime configuration does not expand the diagnostic
vocabulary; custom prefixes require explicit review. TCP tests verify the Cortex
labels in request logs and telemetry while rejecting hostile method suffixes.
Real listener tests also capture accept/listen/task-removal/drain failures and missing,
malformed, or wrong-password certificate diagnostics, and verify exclusive socket
rebinding after cleanup. Tests cover omitted method fields and both banned-IP privacy
paths. Shared TLS/cryptographic categories are checked across Stratum/RPC projections.
The lightweight `scripts/release/test-stratum-diagnostic-sources.py` CI guard pins
reviewed direct logger occurrence counts in the server, connection and projection
files. Both existing identical TLS-ban messages are explicitly listed; adding or
removing an occurrence requires review. Negative fixtures cover duplicated/missing
approved accesses and exception/request/token overloads. A narrow call scan across
all Blockchain/Mining C# sources rejects known raw miner-identity/request fields in
direct logger calls, including multiline/nested expressions. Inline
`LogManager.GetCurrentClassLogger().…` calls are also rejected in these scopes.
The scan does not derive its scope from `identity withheld` markers that a regression
could remove. It is not a full C# parser or taint analyzer: aliases, indirect consumers
and new language constructs still require review and captured-output tests.

Run the focused tests plus existing lifecycle and share-rejection regressions:

```powershell
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -p:BuildOdoCryptWindows=false --filter "FullyQualifiedName~Miningcore.Tests.Stratum|FullyQualifiedName~RpcConsumerDiagnosticTests"
```

The opt-out is for managed-only Windows testing, not a release packaging instruction.
The Windows CI lifecycle selection already excludes
`RunAsync_WithPasswordProtectedPfx_CompletesTlsHandshake` (certificate rotation);
apply that existing exclusion when reproducing the supported Windows lane. The
Linux selection includes it. No new security test is skipped on either platform.
On Linux use the documented source-build/native dependencies before the full suite.
The documented WSL lab can execute the same isolated socket tests without replacing
`/opt/miningcore`, reading live pool secrets, starting payouts or changing the regtest
database. A daemon-backed mining run is not needed to prove this output-only boundary;
existing admission, share, accounting and lifecycle suites remain regression gates.
