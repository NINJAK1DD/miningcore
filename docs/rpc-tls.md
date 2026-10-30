# Daemon RPC TLS verification

Daemon HTTPS (including the HTTPS transport selected by `http2`) and secure
WebSocket subscriptions (`wss`) use .NET's platform certificate validation.
Certificates must be valid for the configured hostname or IP address, within
their validity period, and chain to a trusted certificate authority. The shared
RPC transports no longer accept every certificate through unconditional
validation callbacks. This covers every coin family using `RpcClient` and the
separate generated Ergo and Alephium HTTP clients. Kaspa's gRPC client already
uses default server-certificate validation.

Daemon JSON-RPC HTTP clients do not follow redirects. Ergo's `api_key` and
Alephium's `X-API-KEY` headers stay on requests to the configured endpoint.
The shared RPC client also keeps wallet passwords and payment parameters in
POST bodies from being replayed to a redirected origin by HTTP 307/308.
If a daemon or reverse proxy previously redirected these requests, configure
`daemons[].host` and its port/path to the final endpoint; a 3xx response now
surfaces as a daemon error. [.NET redirect handling](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0)
otherwise retains custom headers and can resend POST bodies when it
automatically follows redirects, including to another HTTPS origin.

### Beam, Conceal and Warthog REST endpoints

The Beam explorer, Conceal daemon and Warthog node REST clients now use the
selected daemon endpoint's `host`, `port`, `ssl` and `http2` settings. With both
flags false they use HTTP/1.1. `ssl: true` selects HTTPS with ordinary platform
certificate verification. `http2: true` selects HTTPS and requires HTTP/2
negotiation; it works with `ssl: false`. REST paths start at `/`; `httpPath`
continues to configure JSON-RPC routes (such as Conceal's `/json_rpc`), not a
REST prefix. For Beam the applicable endpoint has category `explorer`; for
Conceal and Warthog it is the uncategorized daemon endpoint. Both job and
payout clients use this same contract. REST APIs must be exposed at the root
of a dedicated host/port: `httpPath` cannot mount REST under a proxy sub-path.
Warthog WebSocket settings are separate: `portWs`, `httpPathWs` and `sslWs`
select its WS listener and security. Changing `ssl` or `http2` on the REST
endpoint does not change Warthog's WS transport. When moving both through a
TLS proxy, configure `sslWs: true` and the proxy's WS port/path separately;
Warthog uses the daemon endpoint's `host` for both REST and WS.

The published native APIs describe HTTP listeners for
[Beam explorer](https://github.com/BeamMW/beam/wiki/Beam-Node-Explorer-API),
[Conceal](https://github.com/ConcealNetwork/conceal-api) and
[Warthog](https://docs.warthog.network/developers/api/rest/). They do not
document native HTTPS or HTTP/2 switches for these REST listeners. Use a trusted
reverse proxy with TLS termination for HTTPS, and an HTTP/2-capable proxy for
`http2: true`. Keep the native HTTP listener reachable only from that proxy on
a protected local/private path. Point `host` and `port` at the proxy's final
HTTPS listener and provision a certificate trusted by the Miningcore service
whose SAN matches `host`. An HTTP-only endpoint will fail its HTTPS handshake;
Miningcore will not fall back to plaintext. If HTTP/2 is requested, a proxy
that only negotiates HTTP/1.1 will fail instead of silently downgrading.
On Conceal, `http2: true` also affects `/json_rpc`, but that shared RPC client
can negotiate HTTP/1.1 while the REST client requires HTTP/2. Ensure the
proxy supports HTTP/2 for both routes, or leave `http2: false`.

**Upgrade existing configurations before restarting.** Previously, `ssl: true`
or `http2: true` did not protect these REST calls: they still used HTTP. They
now take effect. For a native HTTP-only deployment, first configure the TLS
proxy and update the endpoint host/port and service trust store. Test its final
URL directly, without relying on an HTTP redirect. If you intentionally retain
HTTP on a protected local/private path, set both flags false; review RPC
connections sharing that endpoint because those flags also affect their
transport. A redirect response now surfaces as an error. Configure the
final listener directly, including the final host and port. The REST handler
refuses all redirects so a wallet-related URL, a payment POST body, or
custom headers cannot be replayed to a different origin by a 3xx response.
REST transport warnings identify the configured authority and fixed failure
category without logging paths, query strings, response bodies or credentials.
TLS and HTTP/2 negotiation warnings appear once per outage and rearm after a
response; redirect warnings surface a refused 3xx status.

Warthog's wallet address check runs locally and does not send the configured
private key to the daemon; see
[Warthog wallet key security](warthog-wallet-key-security.md) for incident response.

The trust configuration includes the operating system trust store available to
the Miningcore service (inside its container when applicable). On Linux, .NET
can also use the service account's `CurrentUser\Root` certificate store. For a
private CA,
provision its public CA certificate through your host/container's supported
trust-store mechanism before starting Miningcore. Keep the CA private key off
the pool host. Ensure the daemon/proxy serves the required intermediate chain
and a server-authentication certificate whose subject alternative names match
`daemons[].host`. A DNS name and an IP address are distinct certificate identities.

For Ubuntu, place the verified private CA certificate in a `.crt` file under
`/usr/local/share/ca-certificates/`, run `update-ca-certificates` as administrator,
and restart the service. Apply this inside a custom container image when the
service runs in Docker; modifying only the host's trust store does not update
the container. On Windows, provision the CA in the trusted roots available to
the service account using your organization's certificate-management policy.
Only trust a CA whose identity was verified through an independent channel.

Upgrading can stop HTTPS/WSS endpoints that previously worked with self-signed,
expired, untrusted or mismatched certificates. Replace/renew the server
certificate or provision its verified private CA, then restart Miningcore to
retire pooled connections and reconnect subscriptions. Do not work around a
validation failure by disabling TLS or adding a permissive callback. Plain
HTTP/WS configuration retains its existing behavior and does not provide TLS
protection; keep such daemon connections on a protected local/private path.

`http2: true` selects HTTPS for RPC even when `ssl: false`; WebSocket
subscriptions select `ws://` or `wss://` from `ssl` alone. Set `ssl: true`
when the subscription must use TLS. If WSS certificate verification fails,
Ethereum, Xelis and Warthog continue polling while subscription retries log an
Error about every five seconds. Shares can still flow during this degraded mode.
An HTTP TLS transport failure emits a structured Warning once per outage per
daemon client; receiving an HTTP response resets the warning state even when
the response body is invalid JSON-RPC. Overlapping requests can produce an
additional warning if an earlier failure completes after a successful response.
Alephium startup checks also warn once per outage. WSS emits an Error for each
failed subscription attempt. The fixed `failure: "tls-handshake"` category
means the handshake did not complete: certificate rejection, protocol mismatch,
or a connection dropped during the handshake can produce it on any platform.
The shared failure classifier also affects Stratum and payment diagnostic
summaries when a TLS failure is wrapped by another exception.
Inspect daemon trust, name, validity, network and protocol settings without
copying credentials into logs.
An expired certificate may not be reported until a pooled TLS connection is
replaced and a new handshake occurs; monitor certificate expiry independently
of Miningcore logs.

No new application trust override or insecure-skip option is introduced.
Platform TLS protocol defaults are retained. Certificate revocation is not
checked by default by these .NET HTTP and WebSocket clients; this change does
not establish a separate revocation or client-certificate policy. HTTP basic
authentication and websocket payload handling otherwise retain their contracts.
The application schema and database require no migration.

## Validation

`RpcTlsTests` exercises real loopback TLS for single HTTPS RPC, HTTPS selected
by `http2` (negotiating HTTP/2), batch HTTPS RPC, WSS, Ergo and Alephium, each
with trusted, untrusted, expired, wrong-name, DNS-only and IP-only certificates.
Valid certificates allow the actual RPC/subscribe path; invalid certificates
must fail before the server receives credentials, JSON-RPC requests or
websocket upgrades. On Linux, HTTP rejection retains an authentication failure
as its structural cause. Windows Schannel can report the failure through the
bounded `SecureConnectionError` transport category instead.

Every client runs in a fresh child process. Linux `SSL_CERT_FILE` and
`SSL_CERT_DIR` point the child at an ephemeral CA and empty CA directory;
the service account's .NET `CurrentUser\Root` store can still be consulted.
The test never changes the parent environment or machine/user trust stores.
Processes, servers, keys and temporary files have bounded cleanup. The full
trusted/untrusted matrix runs on Linux/OpenSSL. A separate Windows/macOS smoke
test rejects an untrusted certificate through each client without changing a
platform trust store; Windows and macOS CI run that test. Provisioning a
private CA in Windows/macOS trust stores is not independently certified by
this fixture.
Existing HTTP/WS transport, cancellation, RPC compatibility and diagnostic
tests cover unchanged cleartext behavior.

The REST transport matrix in `RpcTlsTests` exercises Beam, Conceal and
Warthog paths over real loopback HTTPS with trusted, untrusted, expired and
wrong-name certificates, plus HTTPS selected by `http2` with actual HTTP/2
negotiation. An HTTP/1.1-only HTTPS server cannot receive a request when
`http2` is required. Rejected handshakes deliver no request to the server. The
`DaemonRestTransportTests` fixture uses two real HTTP listeners to verify
cleartext compatibility and that 301/302/303/307/308 responses never replay
GET URLs, headers or POST payment data to another origin.
`DaemonRestConsumerTests` exercises Beam explorer selection, Conceal's separate
JSON-RPC path, and the actual Warthog payout POST. Run these on the
documented Ubuntu test lab with:

```bash
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~RpcTlsTests|FullyQualifiedName~DaemonRestTransportTests|FullyQualifiedName~DaemonRestConsumerTests'
```

Reproduce on the documented Linux lab after building the solution:

```bash
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~RpcTlsTests|FullyQualifiedName~RpcDiagnosticTests'
```

The implementation follows the platform contracts for
[HTTP certificate validation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.servercertificatecustomvalidationcallback?view=net-10.0)
and [WebSocket certificate validation](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.remotecertificatevalidationcallback?view=net-10.0).
The [HTTP revocation check](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.checkcertificaterevocationlist?view=net-10.0)
and [cross-platform trust-store behavior](https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography)
are documented by .NET.
