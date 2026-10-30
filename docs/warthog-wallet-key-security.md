# Warthog wallet private key security

Miningcore derives the configured Warthog pool address locally from
`paymentProcessing.extra.walletPrivateKey` and checks it against the pool
`address` before contacting the daemon. The key is used locally to sign payout
transactions. The node receives the signed transaction, never the key. Startup
rejects malformed keys and address mismatches with a generic message that does
not include the key.

The wallet key is still a signing secret in the pool configuration. Limit access
to the configuration file, backups, process memory, and any system that can read
them. Do not include a real key in an issue, test, support request or command
line. Warthog REST transport and redirect hardening is tracked separately in
[#235](https://github.com/NINJAK1DD/miningcore/issues/235). Until that work is
complete, put the Warthog REST daemon on a trusted loopback or private path.

## Response to previous exposure

If a key may have appeared in logs, reverse-proxy access records, network
captures, monitoring, or traffic over an untrusted network, treat it as
compromised. Restrict access to historical logs and backups immediately. Find
all places the old key may have been copied, apply the appropriate retention and
incident-response policy, and rotate or replace the wallet key using the
chain's supported wallet procedure. Verify the replacement address and update
the pool's `address` and `walletPrivateKey` together before resuming payouts.
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
pin, and signature. None sends or serializes the wallet key. `SimpleRestClient`
may still follow redirects until #235 is completed, so keep the daemon on a
trusted path; its Warthog requests contain no key for a redirect to replay.

## Repeat the live check

The opt-in `LiveDaemon_LocalAddressCheckIsAcceptedWithoutSendingKey` test starts
an isolated, temporary testnet daemon on loopback. Set
`MININGCORE_TEST_WARTHOG_DAEMON` to a locally verified `wart-node` executable
before running the Warthog wallet address test filter in the Ubuntu test lab.
The test uses only a synthetic
key and records every request path. It checks that both payout and job startup
accept the derived address while no request or log contains the key. The
ordinary test run exercises the same startup paths with a fake daemon even
when the live daemon variable is unset.
