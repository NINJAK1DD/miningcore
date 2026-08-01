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
| `shareRecoveryFile` | Write-through emergency journal used when PostgreSQL is unavailable |
| `shareRecoveryStateDirectory` | Independent service state for persistent fatal latches |
| `shareRelay` / `shareRelays` | Advanced distributed sender/receiver topology |

Do not store a production configuration in Git. It contains database, daemon, mail and possibly TLS
secrets. Restrict the file to the service account.

## Log files and rotation

Every file configured by `logging.logFile`, `logging.apiLogFile` or `logging.perPoolLogFile` is
rotated by Miningcore through NLog. An active file is archived before a write that would grow it
beyond 512 MiB, and no more than four archives are retained for each file target. Existing active
logs are continued across an ordinary application restart instead of being archived merely because
Miningcore started.

Configure a distinct physical path for every enabled target. In particular, do not point
`logging.apiLogFile` and `logging.logFile` at the same file because their independent writers and
archive lifecycles would compete. Set `logging.apiLogFile` to `null` when API events should flow into
the main log instead of a separate API file.

Do not apply an external `logrotate` rule using `copytruncate` to these same files. Truncating an open
file underneath NLog can leave the process writing at its previous offset and create a large sparse
file. A `postrotate` service restart avoids that offset problem but disconnects miners and resets
in-memory vardiff state, so it is not an appropriate routine rotation mechanism. Remove or disable
any old Miningcore-specific external rotation rule after deploying native rotation; continue to use
the operating system's normal rotation for unrelated service logs.

Capacity planning must include the active file plus up to four archives for every enabled target.
Enabling the main, API and per-pool files creates separate retention sets. Monitor both free bytes
and free inodes on the filesystem selected by `logging.logBaseDirectory`.

## Share recovery storage

`shareRecoveryFile` is the write-through emergency journal used after PostgreSQL share persistence
exhausts its retries. Configure an absolute path so service working-directory changes cannot make
the journal difficult to locate. Miningcore logs the resolved path when the Share Recorder starts.
Restrict the file and its parent directory to the service account because share records are
financial accounting data.

For production, place the journal on a separately monitored filesystem or storage volume from
PostgreSQL data and Miningcore logs when possible. The objective is to preserve a writable failure
domain when database or log storage fills. If separate storage is unavailable, reserve capacity for
the journal and alert on both free bytes and free inodes well before exhaustion. Rotation of
Miningcore logs does not bound the journal or reserve space for it.

Each caught append or force-flush failure is rolled back to the file's previous length and durably
flushed. New journals begin with a format/version magic line before any explanatory text. New
batches are framed by comment records containing the expected record count and SHA-256 hash. Before
appending, Miningcore validates both the newline boundary and the most recent framed batch. The same
validation runs during normal startup, so a crash cannot hide a partial framed append or torn first
append merely because PostgreSQL recovered before the next fallback. Older journals without
framing retain the legacy newline-only check until their first new framed batch. A newline alone is
not proof that an older multi-record append completed.

If PostgreSQL and the journal are both unavailable, Miningcore immediately closes Stratum response
and share-ingress gates, writes an independent fatal latch under
`shareRecoveryStateDirectory/share-recovery-fatal`, attempts the distinct
`Fatal share-recovery fallback failure` administrative notification for up to five seconds, and
exits with status 74. The latch filename is the SHA-256 of the absolute configured journal path and
its content records both that path and hash. Under the supplied systemd unit the state directory is
`/var/lib/miningcore`; outside systemd, set `shareRecoveryStateDirectory` explicitly when the
platform application-data default is unsuitable. Keep this state on service-owned storage that is
independent of the journal's expected failure domain. Container deployments should mount that state
directory on persistent storage so replacing the container cannot discard an unreconciled latch.

The supplied unit has `RestartPreventExitStatus=74`, while the independently stored latch blocks
every normal startup, including relay configurations. An inaccessible or uncertain state directory
also blocks startup. Notification delivery can still fail or time out; the fatal log, exit status
and latch are the authoritative signals. Preserve and reconcile the journal before deleting only
the exact fatal-state path reported by Miningcore as an explicit operator acknowledgement.

The normal `Share Recorder Policy Fallback` event confirms that one fallback batch was force-flushed;
it is not proof that every share throughout an outage reached the journal. Review the complete
incident log and follow the [disk-exhaustion recovery runbook](database.md#recover-after-disk-exhaustion)
before importing or restarting.

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
