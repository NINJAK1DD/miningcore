# Configuration guide

Miningcore reads JSON with comments. Start with [`config.example.json`](../config.example.json), copy
it to `config.json`, and replace every `CHANGE_ME` placeholder. Strict JSON editors may complain about
comments even though Miningcore accepts them.

The exhaustive machine-readable reference is [`config.schema.json`](../src/Miningcore/config.schema.json).
Coin-family extensions are intentionally flexible and may also be documented beside their implementation.

## Main sections

| Section | Purpose |
| --- | --- |
| `logging` | Console/file output and log level |
| `api` | Public REST API, admin/metrics ports, TLS and rate limits |
| `persistence.postgres` | Database connection used for shares, blocks and payments |
| `paymentProcessing` | Cluster-wide payout scheduler |
| `statistics` | Hashrate window, update interval and retention |
| `banning` | Cluster-wide junk, login and invalid-share policy |
| `notifications` | Email, Pushover and administrative events |
| `pools` | Wallet, Stratum ports, daemons, payout policy and coin-specific options |
| `shareRelay` / `shareRelays` | Advanced distributed sender/receiver topology |

Do not store a production configuration in Git. It contains database, daemon, mail and possibly TLS
secrets. Restrict the file to the service account.

## Pool basics

Every enabled pool needs a unique `id`, a matching entry from `coins.json`, a pool wallet `address`,
one or more daemon RPC endpoints, at least one Stratum port in `ports`, and an appropriate
`paymentProcessing` section.

The configured Stratum `difficulty` is the initial fixed difficulty. A `varDiff` block allows the pool
to adjust it toward a target share interval. A miner can request a supported starting difficulty with
`d=VALUE` in its password.

## Bitcoin-family payout precision

Bitcoin-family payouts truncate each positive miner balance to the coin template's
`payoutDecimalPlaces` before calling the wallet. The submitted amount is also the amount written to
payment history and subtracted from the miner balance, so Miningcore never requests more than the
miner is owed. Any sub-precision residual remains on the balance for a later payout.

If a Bitcoin-family template omits `payoutDecimalPlaces`, Miningcore uses four decimal places. The
bundled Litecoin and Dogecoin templates currently use that fallback even though their wallets can
accept additional decimals. Treat this value as Miningcore payout policy, not as a statement of wallet
capability. Choose an explicit value in a custom coin template only after testing the wallet and
payout workflow, and keep `minimumPayment` compatible with the chosen precision.

Truncation can leave a residual after every payment. It is carried into a later qualifying payout,
but can remain indefinitely when a miner stops before reaching the threshold again. When every
selected balance is below the configured precision, Miningcore skips wallet submission and logs the
active `payoutDecimalPlaces` value so the operator can review `minimumPayment`.

## Kaspa multi-transaction payouts

Kaspa wallet can auto-compound a large logical payout into an ordered transaction chain. Miningcore
requires the wallet to return exactly one distinct, nonblank transaction ID for every signed
transaction submitted. A null, partial, blank or duplicate identity response is financially
uncertain and stops payout processing without resetting the miner balance.

The wallet appends the recipient-facing merge transaction after its prerequisite split
transactions. Miningcore therefore stores the final returned ID as the payment-history confirmation
and payment-batch idempotency key. It retains the complete ordered ID list in success notifications
and administrative reconciliation so every prerequisite transaction remains inspectable. This
policy follows the upstream wallet's ordered
[`broadcast`](https://github.com/kaspanet/kaspad/blob/v0.12.23/cmd/kaspawallet/daemon/server/broadcast.go)
and
[`split/merge`](https://github.com/kaspanet/kaspad/blob/v0.12.23/cmd/kaspawallet/daemon/server/split_transaction.go)
implementations.

## LTC/DOGE merged mining

Both the Litecoin parent pool and Dogecoin auxiliary pool must be enabled and use `SOLO`. The parent
pool contains:

```json
"mergedMining": {
  "enabled": true,
  "auxPoolId": "doge-solo",
  "addressParameter": "doge",
  "requireAuxAddress": true,
  "auxiliaryTemplatePollTimeoutMs": 500
}
```

`auxPoolId` must exactly match the Dogecoin pool `id`. `addressParameter` controls the password name;
the recommended default is `doge`. `requireAuxAddress: true` rejects miners that omit a DOGE payout
address. The template poll timeout is milliseconds and may be raised for a healthy but slower local
daemon.

Miner examples:

```text
# Vardiff/default pool difficulty
Username: YOUR_LTC_ADDRESS.rig01
Password: doge=YOUR_DOGE_ADDRESS

# Explicit starting difficulty
Username: YOUR_LTC_ADDRESS.rig01
Password: d=65536;doge=YOUR_DOGE_ADDRESS
```

The LTC address receives an accepted Litecoin parent reward; the DOGE address receives an accepted
Dogecoin auxiliary reward. They are validated independently. Read
[`merged-mining-litecoin-dogecoin.md`](merged-mining-litecoin-dogecoin.md) for daemon, persistence,
reconciliation and deployment requirements.

## Isolated Bitcoin-family regtest

Miningcore normally waits for every Bitcoin-family daemon to have at least one peer before starting
a pool. A deliberately isolated regtest daemon can opt out of that readiness check:

```json
"extra": {
  "allowPeerlessRegtest": true
}
```

The option is disabled by default and is honored only when `getblockchaininfo` reports `regtest`.
It cannot bypass the peer requirement on mainnet, testnet or an unidentified legacy daemon. Do not
enable it for production pools.

## Validate changes safely

1. Keep a known-good copy of the current configuration outside the repository.
2. Edit one logical area at a time.
3. Start Miningcore in the foreground and read every startup warning.
4. Check `/api/health-check` and `/api/pools`.
5. Connect a test miner at low risk before moving production traffic.
6. For merged mining, repeat the documented regtest and schema preflight when topology changes.
