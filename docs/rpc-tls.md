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

### REST clients outside this change

Beam explorer, Conceal and Warthog node REST requests use `SimpleRestClient`,
not the JSON-RPC or generated clients described above. Their current base URLs
are always plain HTTP and their default HTTP client can follow redirects, even
when the daemon endpoint sets `ssl: true` or `http2: true`. For Warthog,
`ssl: true` can protect its WSS subscription while its node REST traffic
remains plaintext. Keep these REST endpoints on a trusted loopback or private
path until [REST transport hardening #235](https://github.com/NINJAK1DD/miningcore/issues/235)
is complete. Warthog's wallet address check now runs locally and does not send
the configured private key to the daemon; see
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
