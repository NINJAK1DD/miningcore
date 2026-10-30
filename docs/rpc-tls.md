# Daemon RPC TLS verification

Daemon HTTPS (including the HTTPS transport selected by `http2`) and secure
WebSocket subscriptions (`wss`) use .NET's platform certificate validation.
Certificates must be valid for the configured hostname or IP address, within
their validity period, and chain to a trusted certificate authority. The shared
RPC transports no longer accept every certificate through unconditional
validation callbacks. This affects every coin family using `RpcClient`.

The trust configuration is the operating system trust store available to the
Miningcore service, or the trust store inside its container. For a private CA,
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

No new application trust override or insecure-skip option is introduced.
Platform TLS protocol and revocation defaults are retained; this change does
not establish a separate revocation or client-certificate policy. HTTP basic
authentication and websocket payload handling otherwise retain their contracts.
The application schema and database require no migration.

## Validation

`RpcTlsTests` runs 16 real loopback TLS cases: single HTTPS RPC, HTTPS selected
by `http2` (negotiating HTTP/2), batch HTTPS RPC and WSS, each with trusted,
untrusted, expired and wrong-name server certificates. Valid certificates allow
the actual RPC/subscribe path; invalid certificates must fail before the server
receives credentials, JSON-RPC requests or websocket upgrades. HTTP rejection
must retain an authentication failure as its structural cause.

Every client runs in a fresh child process. Linux `SSL_CERT_FILE` and
`SSL_CERT_DIR` point only that child at an ephemeral CA and empty CA directory.
The test never changes the parent environment or machine/user trust stores.
Processes, servers, keys and temporary files have bounded cleanup. These tests
run on Linux/OpenSSL and explicitly skip elsewhere; Windows/macOS platform
trust-store provisioning is not independently certified by this fixture.
Existing HTTP/WS transport, cancellation, RPC compatibility and diagnostic
tests cover unchanged cleartext behavior.

The Ubuntu WSL lab passed all 135 TLS/RPC/daemon-error cases with zero skips.
Restoring both original permissive callbacks in the disposable lab made all 12
invalid-certificate cases fail while the four trusted cases passed. Restoring
the fix passed all 16 certificate cases again. These negative controls confirm
the assertions detect the original bypass. Managed sources were rebuilt; the
lab reused existing native outputs, and full CI independently rebuilds them.

Reproduce on the documented Linux lab after building the solution:

```bash
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~RpcTlsTests|FullyQualifiedName~RpcDiagnosticTests'
```

The implementation follows the platform contracts for
[HTTP certificate validation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.servercertificatecustomvalidationcallback?view=net-10.0)
and [WebSocket certificate validation](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.clientwebsocketoptions.remotecertificatevalidationcallback?view=net-10.0).
