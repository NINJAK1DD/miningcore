# Warthog wallet private key security

Miningcore derives the configured Warthog pool address locally from
`paymentProcessing.extra.walletPrivateKey` and checks it against the pool
`address` before contacting the daemon. The key is used locally to sign payout
transactions. The node receives the signed transaction, never the key. Startup
rejects malformed keys and address mismatches with sanitized messages that do
not include the key. The configured pool address must match the lowercase
checksummed address exactly when payment processing is enabled for both the
cluster and the pool; a mixed-case address is then rejected at startup.

The wallet key is still a signing secret in the pool configuration. Limit access
to the configuration file, backups, process memory, and any system that can read
them. Do not include a real key in an issue, test, support request or command
line. Warthog REST now honors the endpoint TLS settings and refuses redirects;
see the [REST migration guide](rpc-tls.md#beam-conceal-and-warthog-rest-endpoints).
Keep the native HTTP daemon listener on a trusted loopback or private path,
including the proxy-to-daemon segment when a TLS proxy is used.

## Response to previous exposure

On versions before this fix, every successful start with Warthog payments enabled
sent the configured private key in a plaintext HTTP URL to the daemon. Each
payout cycle that configured its handler repeated that request. Assume the key
was exposed if such a pool ran, even if no error was reported. Inspect Miningcore
application logs and error diagnostics, Warthog daemon debug or HTTP access logs,
reverse-proxy and load-balancer access logs, observability/monitoring records,
network captures, and any non-loopback network path between pool and daemon.
Check historical copies and backups of those records as well.

Restrict access to those records and backups immediately. Apply the appropriate
retention and incident-response policy, and rotate or replace the wallet key
using the chain's supported wallet procedure. Verify the replacement address
and update the pool's `address` and `walletPrivateKey` together before resuming
payouts.
The old wallet address will not automatically transfer funds to a new address;
plan and verify that transfer according to Warthog's supported procedure. Do
not paste either key into diagnostics while investigating.

The local address check follows Warthog's
[wallet integration specification](https://docs.warthog.network/developers/integration/wallets/):
the compressed secp256k1 public key is hashed with SHA-256 then RIPEMD-160;
the first four bytes of SHA-256 of that 20-byte hash are appended as a checksum.
Warthog's [REST API documentation](https://docs.warthog.network/developers/api/rest/)
warns that the private-key wallet endpoint is only for testing and should not
be used for wallet operations involving a private key.

The Warthog request audit covers startup address checks, balance lookup,
transaction preparation, and payout submission. After this change, startup
uses only the public pool address for `/chain/mine/{address}`; balance lookup
uses `/account/{address}/balance`; transaction preparation requests chain and
fee data; payout submission serializes the destination, amount, fee, nonce,
pin, and signature. None sends or serializes the wallet key. Daemon REST
requests refuse redirects, so those fields are not replayed to a new origin
by the HTTP client. Keep any native HTTP listener on a protected path.

## Repeat the live check

The opt-in `LiveDaemon_LocalAddressCheckIsAcceptedWithoutSendingKey` test starts
an isolated, temporary testnet daemon on loopback. Set
`MININGCORE_TEST_WARTHOG_DAEMON` to a locally verified `wart-node` executable
before running the Warthog wallet address test filter in the Ubuntu test lab.
The test uses only a synthetic
key and records each request method, complete URI, headers, and body. It checks
that both payout and job startup accept the derived address while no request or
log contains the key. The
ordinary test run exercises the same startup paths with a fake daemon even
when the live daemon variable is unset.
