# Stratum transport and client bans

Miningcore keeps the normalized socket transport address separately from the
effective client address. Trust is frozen per listener at startup from
`tcpProxyProtocol.enable` and `proxyAddresses`; only a peer in that enabled
allowlist can supply a PROXY identity. Omitted or empty lists still trust localhost.
Restart to change that policy. Before starting any pool, Miningcore freezes the
normalized union of trusted peers on every enabled internal Stratum listener in
every enabled pool. All automatic bans exclude that union, including direct
connections to other listeners and forwarded claims of a different proxy's address.
Disabled pools, external Stratum pools and disabled PROXY policies add no peers.
IPv4-mapped IPv6 addresses use the IPv4 ban key.

| Connection | Effective identity for admission and request-time ban checks | Target of an automatic client ban |
| --- | --- | --- |
| Direct peer, or PROXY disabled | Normalized socket peer | Socket peer, unless trusted as a proxy elsewhere in the cluster |
| Untrusted peer on optional PROXY listener, without a header | Normalized socket peer | Socket peer, unless trusted as a proxy elsewhere in the cluster |
| Trusted peer with valid TCP4/TCP6 header | Normalized forwarded source | Forwarded source, unless it is any trusted proxy address in the cluster |
| Trusted peer before identity, including TLS/setup failure | Socket peer until a header is validated | None |
| Trusted peer with optional/headerless session | Socket peer | None |
| Trusted peer with `PROXY UNKNOWN` | Socket peer; ignored header fields cannot identify a client | None |
| Untrusted, malformed, missing mandatory, or oversized PROXY header | No request dispatched; no claimed identity installed | No new automatic ban rule |

An existing ban on the socket transport always rejects it before connection
registration, admission, TLS or request buffering. Immediately after trusted
header validation, Miningcore checks both the normalized effective identity and
the transport address again. A banned identity is disconnected on the header
alone, before address admission and before parsing following JSON, including
coalesced data. This refusal does not extend the ban or create another ban.

Before every complete request reaches a pool handler, Miningcore checks both
addresses. A client or transport ban applied after startup therefore stops the
next request. Bans do not interrupt accounting work already owned by a handler
and do not proactively disconnect an idle established connection. Admission leases
remain held until dispatch and owned handlers drain, including after a refusal.
Other clients through the same unbanned proxy, and other pools, retain their normal
admission capacity and accounting lifecycle.

All automatic bans from junk/TLS failures, login failures, invalid shares and
miner effort use the same client-attribution guard. If a trusted session never
establishes a distinct TCP4/TCP6 client, Miningcore still rejects or disconnects
the offending session according to the existing failure policy, but does not
ban its shared transport. A validated claim of the proxy's own address also does
not authorize an automatic transport ban. Operators can intentionally block a
proxy at the firewall; that blocks all clients using the transport. The shipped
integrated manager has no administrative API or configuration for inserting bans.
Extensions can insert explicit bans programmatically through `IBanManager.Ban`;
trust does not exempt a proxy from such an existing ban. `IpTables` is an enum
option without a shipped manager implementation, not an operator ban control.

The existing `IBanManager` interface and cluster-wide address-based ban namespace are unchanged:
an explicitly banned address is rejected whether used as a socket peer or as a
validated forwarded source. Existing bans are neither cleared nor rewritten.
A ban on one client address can still affect that same client address in other
pools; healthy identities retain their normal pool admission and accounting behavior.
Configured ban durations and enable flags remain unchanged. In particular, the
legacy junk policy does not ban when the cluster `banning` object is absent;
an existing object with unset/true `banOnJunkReceive` enables the three-minute
junk/TLS ban, and false disables it. Malformed PROXY and oversized requests remain
disconnect-only failures. Startup deadline, shutdown and fail-stop cancellation
remain non-banning outcomes. The integrated manager exempts exactly `127.0.0.1`
and `::1`, rather than the entire IPv4 loopback range.

When a ban expires, the next connection or request can proceed subject to normal
admission limits; no restart is required. Banned-client refusals still consume
the pool's transport allowance, but do not consume forwarded address tokens or
retain address ledger entries. A failed connection releases its pending identity
and concurrency leases through the existing dispatch observer.

Miningcore's TLS ordering is unchanged: when it handles TLS, it authenticates the
TLS stream before reading a PROXY line inside it. A cleartext PROXY header before
the TLS handshake is unsupported. TLS does not authenticate a claimed client IP;
attribution comes from the configured socket-peer trust boundary and strict
header validation. Configure mandatory headers on dedicated private listeners
and restrict network access to the proxies. A trusted front end must overwrite
untrusted client-supplied PROXY lines, enforce handshake/rate limits itself and
must not multiplex different client identities on one Stratum connection.

These choices follow the [HAProxy PROXY protocol specification](https://www.haproxy.org/download/1.8/doc/proxy-protocol.txt)
on trusted senders, complete header validation and `UNKNOWN`. TLS setup uses
[.NET's server authentication API](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream.authenticateasserverasync?view=net-10.0).
PROXY v2, certificate-based proxy authentication and changes to TLS framing are
outside this policy.

Positive `Banning…` messages are emitted only after a manager invocation. Suppressed
automatic bans emit an Info message and the fixed Debug event `AutomaticBanSuppressed`.
The counter `miningcore_stratum_automatic_bans_total{pool,outcome}` uses only the
configured pool ID and `applied`, `suppressed` or `unavailable`. `applied` means the
manager call completed; custom manager implementations determine their own storage
semantics. The integrated manager's literal loopback exemptions count as suppressed;
no configured manager counts as unavailable. No address or connection ID is a metric label.

Diagnostics add fixed Debug events `BannedIdentity` and `AutomaticBanSuppressed`,
with only a server connection ID. They contain no request/header/exception text,
client labels or worker credentials. Ban refusal is distinct from admission-limit
refusal; it does not increment an admission refusal reason. Existing address logs
retain the configured IP censorship policy. See [Stratum diagnostics](stratum-diagnostics.md)
and [connection admission](stratum-connection-admission.md).

## Regression validation

`StratumBanAttributionTests.cs` extends the real-listener admission fixture and
uses an actual non-loopback local IPv4 interface, raw TCP and TLS 1.2/1.3 streams.
Without a suitable IPv4 interface these tests report an explicit skip, so an
IPv6-only or network-isolated runner cannot silently substitute an exempt address.
The tests cover header-only rejection, coalesced junk, shared proxies, cross-pool
proxy protection, optional untrusted headerless peers, mapped
addresses, direct clients, late client/transport bans, real integrated-ban expiry,
headerless/`UNKNOWN` sessions, proxy self-address claims, untrusted victim claims,
pre-identity TLS failures, ban configuration and lease cleanup. The existing
admission, accounting, shutdown, diagnostics and coin-pool suites remain gates.

Run in the documented local Ubuntu lab (`scripts/test-lab/README.md` in the
operator checkout), or a provisioned Linux checkout with the same dependencies:

```bash
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~Miningcore.Tests.Stratum'
```

The CI source guard also rejects direct `banManager.Ban` calls outside the central
attribution helper. Its narrow syntax check complements runtime tests and source
review; aliases or new manager fields still require review. Save environment and
test-run evidence with the pull request rather than in this operational guide.
