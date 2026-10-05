# Stratum transport and client bans

Miningcore keeps the normalized socket transport address separately from the
effective client address. Trust is frozen per listener at startup from
`tcpProxyProtocol.enable` and `proxyAddresses`; only a peer in that enabled
allowlist can supply a PROXY identity. Omitted or empty lists still trust localhost.
Restart to change that policy. IPv4-mapped IPv6 addresses use the IPv4 ban key.

| Connection | Effective identity for admission and request-time ban checks | Target of an automatic client ban |
| --- | --- | --- |
| Direct peer, or PROXY disabled | Normalized socket peer | Socket peer |
| Untrusted peer on optional PROXY listener, without a header | Normalized socket peer | Socket peer |
| Trusted peer with valid TCP4/TCP6 header | Normalized forwarded source | Forwarded source, unless it equals the transport address |
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
not authorize an automatic transport ban. An operator who intentionally wants
to block a proxy can still put its normalized address in the ban manager or
deny it at the firewall; that blocks all clients using the transport. Trusting
a proxy does not exempt it from an existing explicit address ban.

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
remain non-banning outcomes. The integrated manager still exempts loopback.

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

Diagnostics add fixed Debug events `BannedIdentity` and `AutomaticBanSuppressed`,
with only a server connection ID. They contain no request/header/exception text,
client labels or worker credentials. Ban refusal is distinct from admission-limit
refusal; it does not increment an admission refusal reason. Existing address logs
retain the configured IP censorship policy. See [Stratum diagnostics](stratum-diagnostics.md)
and [connection admission](stratum-connection-admission.md).

## Regression validation

`StratumBanAttributionTests.cs` extends the real-listener admission fixture and
uses an actual non-loopback local IPv4 interface, raw TCP and TLS 1.2/1.3 streams.
The tests cover header-only rejection, coalesced junk, shared proxies, mapped
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

Validated on 5 October 2026 in the Ubuntu 26.04 WSL lab, with .NET SDK 10.0.112
and runtime 10.0.12. Native and managed builds completed with zero warnings/errors;
the repository warning audit passed. All 25 native libraries and 241 managed entry
points/dynamic relocations passed inventory checks. The full suite passed 3,787
tests with zero failures; 13 optional pinned Knots tests were unavailable and one
benchmark was intentionally skipped. All 40 new cases passed, including the TLS
ban-during-accounting regression; no issue-specific tests were skipped.

The full run enabled Bitcoin 31.1, Litecoin 0.21.5.8, Dogecoin 1.14.9 and PostgreSQL
18 accounting/TLS fixtures. It used a separate loopback PostgreSQL test cluster
with CI-compatible bootstrap administrator roles and CI's `LD_LIBRARY_PATH`
pointing at the managed/native test outputs. The local lab's application-only
database role is unsuitable for the newer administrator/schema-ownership tests.
The temporary cluster was stopped after validation. Documentation links and
Stratum logging guards/negative fixtures also passed.
