# Stratum connection admission

All internal Stratum listeners now apply bounded admission before creating a worker,
receive pipe, send queue or TLS session. A pool shares one policy across all its ports.
Reconnects therefore cannot continually reset their allowance for expensive startup
work. Already admitted connections keep their jobs and submission/accounting path.
This complements the [BLAKE2b difficulty budget](bitcoin-blake2b.md); it does not
replace that per-connection negotiation limit.

## Policy and operator controls

`pools[].connectionAdmission` is optional. Omitting it, or individual properties,
uses these defaults. All limits are enabled; zero does not disable enforcement.
Changes take effect on process restart.

```json
"connectionAdmission": {
  "connectionsPerSecond": 100,
  "burst": 200,
  "maxConcurrentConnections": 4096,
  "connectionsPerSecondPerAddress": 2,
  "burstPerAddress": 32,
  "maxConcurrentConnectionsPerAddress": 256,
  "maxTrackedAddresses": 16384,
  "idleExpirySeconds": 120,
  "startupTimeoutSeconds": 10
}
```

| Control | Meaning |
| --- | --- |
| `connectionsPerSecond`, `burst` | Continuously replenished pool token bucket; a transport admission costs one token, including a subsequent identity/header failure. |
| `maxConcurrentConnections` | Bounds all socket-owning dispatches, including TLS/PROXY startup and draining request handlers. |
| `connectionsPerSecondPerAddress`, `burstPerAddress` | Second token bucket shared by reconnects from one normalized client IP. A successful identity admission costs one token. |
| `maxConcurrentConnectionsPerAddress` | Bounds simultaneously admitted dispatches for that IP; several miners may share it. |
| `maxTrackedAddresses` | Hard cap on active plus retained idle identities. New identities are refused when full; existing identities retain their allowances. |
| `idleExpirySeconds` | Retention after the last dispatch releases the identity. Must be at least `burstPerAddress / connectionsPerSecondPerAddress`. |
| `startupTimeoutSeconds` | Relative deadline covering TLS detection/handshake, optional PROXY header and the first complete JSON request. Partial bytes do not reset it. It stops before invoking the first handler. |

Rate/burst controls accept 1–100,000; concurrency and identity caps accept
1–1,000,000; idle expiry accepts 1–86,400 seconds; startup accepts 1–120 seconds.
These validation ceilings are not capacity recommendations. Size limits from load
measurements, memory headroom and the actual fleet's reconnect behavior.

No rejection waits in an application queue, schedules a retry task, adds an IP ban,
or disconnects another miner. Rate tokens are not refunded on disconnect or failure.
Direct-address refusals happen before transport setup and do not spend pool tokens;
a known exhausted direct address cannot drain other addresses' startup allowance.
Trusted proxy transports spend a pool token before their deferred identity is known,
including when that later identity admission fails.
Concurrency leases are released only after dispatch/request drain and task observer
cleanup, including setup failures. In-flight share persistence continues to its owned
outcome; there is no admission cancellation injected into an established handler.
Policies and address tables are independent between pools in the same process.

Refill, retention and diagnostic suppression use `TimeProvider` monotonic timestamps,
not UTC or the pool's adjustable master clock. Idle state is kept in a bounded ordered
list with exactly one node per idle address. Each accept removes at most 16 expired
entries; one pool timer removes at most 256 per second even without traffic. Expiry
therefore occurs after the configured idle interval plus at most roughly
`ceil(maxTrackedAddresses / 256)` timer ticks under a fully idle table, subject to
scheduler delay. Active identities never expire. Rejections do not extend idle
retention, and expiry cannot restore tokens earlier than a complete natural refill.
State is process-local and intentionally resets on restart; separate processes need
an upstream aggregate policy if they share a protection boundary.

## NAT and proxy identity

Direct clients are keyed by the socket peer IP, with IPv4-mapped IPv6 normalized to
IPv4. Ports, usernames, worker names, passwords and JSON fields cannot create a new
identity. IPv6 addresses use their full address; prefix rotation remains subject to
the pool limits and finite identity table.

For `tcpProxyProtocol.enable=true`, only a peer in `proxyAddresses` may assert an
address. The existing omitted/empty allowlist default trusts exactly IPv4/IPv6
localhost. Configure explicit proxy addresses and `mandatory=true` on dedicated
private listeners; restrict network access to those proxies. Trusting a proxy means
trusting its attribution for every client. Never allow a proxy to forward an
untrusted client's supplied PROXY line unchanged.

Trusted transports still consume the pool rate and concurrency allowance before
TLS or header processing. Their address allowance is deferred until the first
line establishes identity; the proxy itself is not charged for every forwarded
client. Only one complete, valid PROXY v1 header can change identity. Parsing checks
CRLF, the 107-byte wire limit, exact fields, TCP4/TCP6 address families, literal
addresses and decimal ports. Spoofed/untrusted or malformed headers close without
dispatching a request. Scoped IPv6 claims and ambiguous short/octal IPv4 forms are
rejected. `UNKNOWN` retains the transport peer identity. With optional headers, an
ordinary first JSON request likewise uses the peer identity. PROXY v2 is unsupported.
Existing TLS ordering is unchanged: if Miningcore handles TLS, the PROXY line is
inside that TLS stream; a cleartext PROXY preface before a TLS handshake is unsupported.

Many legitimate miners behind NAT share one address allowance. Defaults permit 32
immediate startups, two more per second, and 256 simultaneously active miners on
one address. A larger NAT fleet needs larger address controls plus corresponding
pool capacity. During a simultaneous fleet restart, stagger reconnects with jitter
and allow time for refill. Do not repeatedly restart Miningcore to clear the limit.

A proxy multiplexing several miners onto one Stratum connection has one identity
and one connection allowance; it must not switch PROXY identities per request.
A proxy opening many connections without trustworthy original-IP attribution is
treated as one shared address. Give that deployment an appropriately sized policy
or a separate pool; do not trust arbitrary forwarded metadata to bypass the cap.

## Diagnostics and recovery

Prometheus exports:

| Metric | Labels / meaning |
| --- | --- |
| `miningcore_stratum_connections_admission_total` | `pool`, `reason`: `transport-admitted`, `identity-admitted`, `pool-rate`, `pool-concurrency`, `address-rate`, `address-concurrency`, `address-capacity`. Transport and identity admissions are separate stages, not two connections. |
| `miningcore_stratum_admission_active` | `pool`: dispatches still holding capacity, including drain. |
| `miningcore_stratum_admission_addresses` | `pool`: active and idle identities retained in the finite table. |

Labels contain only configured pool IDs and fixed outcomes, never client addresses,
connection IDs or worker data. Admission emits at most one warning per pool per
monotonic minute, with a fixed reason and number of suppressed rejections. Existing
[Stratum diagnostics](stratum-diagnostics.md) cover framing/TLS errors.

Firmware that reconnects after a refused `d=` authorization or difficulty change
may now see TCP EOF/reset before subscribe receives a reply. This is distinct from
a protocol authorization error: no Stratum retry response can be sent before a
connection is admitted. Back off with jitter; one default address token replenishes
every 500 ms, a full address burst in 16 seconds, and a full pool burst in two seconds.
Continuous competition can consume refilled tokens, so these are not guarantees
that a particular reconnect succeeds. Concurrency refusals recover when owned
dispatches finish; address-capacity refusals recover as idle entries expire.
Inspect the reason counter before changing limits. Fix repeated negotiation loops
or wrong mandatory-PROXY settings instead of treating every reset as a daemon fault.

## Design rationale and limits

A rate limit bounds repeated setup/job creation, but alone cannot bound slow or
retained connections. A concurrent limit bounds live work, but alone permits fast
reconnects to reuse capacity indefinitely. Both are applied without a queue. An
automatic ban-manager penalty was rejected for this policy: banning a shared IP
can eject healthy NAT miners, affect unrelated pools and amplify firmware retries.
Existing operator-configured junk/share bans remain separate and unchanged.

The algorithm comparison follows [Microsoft's rate-limiter guidance](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0#rate-limiter-algorithms).
Identity handling follows the [HAProxy PROXY specification](https://github.com/haproxy/haproxy/blob/master/doc/proxy-protocol.txt),
including its trust and multiplexing constraints. Timing uses the
[.NET timestamp API](https://learn.microsoft.com/en-us/dotnet/api/system.timeprovider.gettimestamp?view=net-10.0).

This bounds application admission and reconnect work, not SYN floods, kernel TCP
state, aggregate bandwidth, all established-session request costs, or host-wide
CPU usage. A hostile shared address can compete with its legitimate miners for
new admissions; a distributed attacker can exhaust a pool's global allowance.
Upstream connection/network protection and per-protocol limits are still needed.

## Validation

`StratumAdmissionTests` uses the production accept loop and TCP dispatch to cover
sustained churn across two ports, concurrent connects, the default shared-address
startup burst, retained miners, independent pools, monotonic refill boundaries,
forward/backward wall-clock changes, bounded identity capacity, timer-driven idle
expiry, proxy trust/parsing, startup deadlines, setup failure, task-observer drain,
and already-owned share persistence after peer disconnect.

`RealListener_ReconnectChurn_MeasuresAndBoundsFreshWorkerJobs` uses the real BLAKE2b
pool and listener with a supplied job/RPC fixture. Each admitted connection
subscribes, spends all eight negotiation tokens, reads responses and disconnects.
Run it alone to avoid contaminating its process CPU/allocation measurements:

```sh
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -c Release --filter FullyQualifiedName~RealListener_ReconnectChurn --logger 'console;verbosity=detailed'
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj -c Release --filter FullyQualifiedName~Miningcore.Tests.Stratum
```

The two policies compare a generous 1,000-connection allowance with an eight-startup
address burst using a frozen monotonic clock. This is a deterministic functional
comparison, not a production throughput benchmark or a measurement of an unpatched
binary. Each admitted session creates exactly nine worker jobs; 256 attempts must
produce 2,304 jobs versus 72. Both must retain zero connections/tasks after drain
and exactly one idle address entry. Measured CPU and allocated bytes include the
test client, runtime/JIT and server in the same process; peak dispatch count is
sampled and asserted retention is the stronger lifecycle check.

For server-process CPU and managed allocation measurements with a separate client:

```sh
python scripts/diagnostics/measure_stratum_churn.py
```

The script starts only the test fixture on an ephemeral loopback port, runs the
client outside the .NET process, verifies all replies/work counts, and closes both
processes. Server figures still include test-host/runtime overhead and JIT; they
are observations, not assertions of hardware-independent throughput.

Recorded 2026-09-18 on Windows with .NET 10.0.11, using that separate-client script:

| Policy | Attempts / admitted | Worker jobs | Server process CPU | Server managed allocation | Elapsed |
| --- | --- | ---: | ---: | ---: | ---: |
| Generous allowance | 256 / 256 | 2,304 | 1,750 ms | 81,100,240 bytes | 1,329 ms |
| Eight-startup address burst | 256 / 8 | 72 | 203 ms | 3,978,888 bytes | 293 ms |

Both runs sampled one peak dispatch, retained zero connections/tasks after drain,
and retained one idle address. The work reduction is deterministic; CPU and allocation
figures vary with the runtime, JIT and host load. This compares configured policies on
the fixed implementation, not an unpatched binary.

The documented lab's real Knots 29.4.1 and a disposable PostgreSQL 17 instance also
passed all nine selected daemon/accounting tests: all payout schemes, real exactly-once
PPS persistence, accepted-proof preservation after terminal publication failure,
VarDiff edge cases and accounting atomicity/idempotency. The temporary instance was
stopped after testing; existing lab services and databases were not changed.

Windows admission/configuration/accounting checks passed. The existing certificate
identity assertion is interfered with by the host's Avast TLS interception; security
software was not disabled. The same TLS assertion and selected listener suite passed
on the documented Ubuntu 22.04 WSL compatibility lab. This evidence does not certify
particular physical miner firmware or production network capacity.
