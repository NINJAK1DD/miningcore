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
| `shareRecoveryStateDirectory` | Independent service state for fatal latches, journal-tail anchors, and import-retirement markers |
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

Miningcore acquires an adjacent exclusive process-lifetime owner file before startup recovery checks
or import inspection. This intrinsic location prevents a different `shareRecoveryStateDirectory`
from creating an independent owner for the same journal. A second local recorder, merged-mining
relay submitter or recovery import using the same journal fails before pools start, even if it uses
different Stratum ports or reaches the parent through a directory symlink. Journal-file symlinks and
hard links are rejected; use one regular-file pathname. The owner is released only after a successful
final recorder drain; process-fatal shutdown retains it until the operating system closes the process
handle. Do not delete the hidden `.miningcore-share-recovery-*.owner.lock` file beside
`shareRecoveryFile` while Miningcore is running.

For production, place the journal on a separately monitored filesystem or storage volume from
PostgreSQL data and Miningcore logs when possible. The objective is to preserve a writable failure
domain when database or log storage fills. If separate storage is unavailable, reserve capacity for
the journal and alert on both free bytes and free inodes well before exhaustion. Rotation of
Miningcore logs does not bound the journal or reserve space for it.

Each caught append or force-flush failure is rolled back to the file's previous length and durably
flushed. A journal's first batch is written to a force-flushed temporary file, atomically renamed to
the active path and followed by a parent-directory `fsync` on Linux, so successful first creation
includes the filename itself in the durability boundary. New journals begin with a format/version
magic line before any explanatory text. Each v2 batch records a contiguous sequence number, the
previous frame digest, record count, record-content SHA-256 and deterministic current-frame digest
in matching start/end markers. Startup, initial fallback entry and recovery import stream the
complete chain and reject duplicated, missing or reordered middle frames as well as mismatched
counts/hashes, nested or unclosed frames, records outside frames and unexpected trailing content.
Record and legacy-prefix hashes deliberately normalise physical line endings to `\n`; they protect
logical record content rather than the original newline bytes. Recovery lines above 1,048,576 characters are
rejected without first allocating the complete hostile line.

After the fallback tail is trusted, append work is linear: under the process-lifetime ownership lock,
exclusive active-file handle and canonical-filename in-process writer gate,
Miningcore verifies the active file identity and expected length, hashes only the new frame, and
advances its cached sequence/digest after the force-flush succeeds. Replacing, truncating or growing
the active file outside Miningcore stops fallback rather than silently resetting that state. Each
force-flushed frame is committed with an atomically replaced sequence/digest anchor below
`shareRecoveryStateDirectory/share-recovery-terminal`. A share diverted to fallback is not
acknowledged until both files are durable. Startup, import and the first runtime append reject a
journal shorter than its anchor, including deletion of an otherwise valid final frame. A missing
anchor is accepted only for legacy/unframed and v1 journals; chained v2 journals fail closed because
that format has always required an anchor.

A bounded 65,536-share persistence queue prevents an unlimited in-memory outage backlog. If it
fills, publication transfers the share to one bounded 1,024-share emergency channel and one journal
writer; filesystem work and waiting happen after the mining admission lock is released. A local
Stratum response waits for that forced append. If both bounded capacities are exhausted, or the
emergency append fails, Miningcore admits no positive response and enters the status-74 fatal path
with the complete unresolved count and pool set. The fatal transition first acquires the exclusive
publication/response gate and waits for earlier admissions to leave it; only then does it snapshot
the unresolved registry and write exact incident evidence. This quiescent ordering excludes shares
whose PostgreSQL commit became known before the gate closed and includes every still-unresolved
share that was published before closure.

The normal 65,536-share queue is intentionally memory-resident for throughput. A positive Stratum
response proves local accounting-pipeline admission, but an abrupt process, kernel or machine loss
can still lose acknowledged entries that had not yet reached PostgreSQL. Treat 65,536 as the maximum
configured volatile exposure, not a crash-durability promise. Graceful shutdown drains active
Stratum request handlers before closing recorder intake, then accounts for every admitted queue item.
An unresponsive handler receives at most five seconds; expiry closes the global admission gate,
marks the process failed and lets Share Recorder retain its reserved persistence/recovery window.

Graceful shutdown first closes share intake, disposes the subscription and completes both writers.
The queue drain does not use the normal `BackgroundService` stopping token. Share Recorder limits
its own PostgreSQL drain to 20 seconds (or the remaining portion of the 45-second host budget,
whichever is shorter), then gives bounded transaction recovery and fatal-state handling at most 15
seconds before force-flushing the complete unresolved registry to the journal. The supplied
90-second systemd timeout provides a further margin for this
recovery work and other hosted services. Shutdown succeeds only after every
admitted share reaches PostgreSQL or the journal; failure of both destinations writes the same
status-74 latch for the full backlog.

Older journals retain the legacy newline-only guarantee for their original unframed prefix and v1
frames. The first v2 frame appended to an older journal anchors the complete normalised legacy
prefix, and all later frames are chained. A pure legacy/v1 source cannot retroactively prove that a
complete frame was never duplicated before the upgrade. The independent terminal anchor detects
offline deletion of complete v2 tail frames, while incident checksums and monitoring remain
necessary for legacy history. A newline alone is not proof that an older multi-record append completed.

If PostgreSQL and the journal are both unavailable, Miningcore immediately closes a coordinated
Stratum acceptance boundary. Every pool family publishes a validated share to the accounting
pipeline before admitting its positive response. Healthy publications and synchronous response-
queue admissions take concurrent read admissions, while fail-stop takes the exclusive transition;
unrelated pools are therefore not serialized by one global monitor. Queued responses are cancelled
directly when the gate closes. Miningcore then writes an
independent fatal latch under
`shareRecoveryStateDirectory/share-recovery-fatal`, attempts the distinct
`Fatal share-recovery fallback failure` administrative notification for up to five seconds, and
exits with status 74. The latch filename is the SHA-256 of the absolute configured journal path and
its content records both that path and hash. Under the supplied systemd unit the state directory is
`/var/lib/miningcore`; outside systemd, set `shareRecoveryStateDirectory` explicitly when the
platform application-data default is unsuitable. Keep this state on service-owned storage that is
independent of the journal's expected failure domain. Container deployments should mount that state
directory on persistent storage so replacing the container cannot discard an unreconciled latch.
The sibling `share-recovery-terminal` and `share-recovery-import` directories are equally
authoritative. All three safety-state subdirectories are pre-created during startup, with every new
directory entry parent-synchronised on Linux before mining is accepted. Do not delete or edit their
path-hashed files independently. Miningcore proves that a terminal or import marker is absent only
after successfully enumerating its exact state directory. Exact state and alias inspection uses
atomic no-follow handles on supported Linux and Windows hosts. A directory, symbolic link,
unsupported file type, malformed marker, disappearing entry, or inaccessible/uncertain directory blocks startup
and the first fallback append instead of being treated as absence. Recovery writes a
pending import marker before opening its PostgreSQL transaction, advances it after commit through
`Committed`, `ArchiveDurable`, `AnchorRetirementAuthorised`, and `AnchorRetired`, then removes it
only after reopening and fully revalidating the retained source, atomically archiving and
directory-syncing it, revalidating the same file object, and retiring the matching anchor. Each
phase retains the validated terminal sequence and digest. Anchor absence is valid on resume only
after durable retirement authorisation, so interruption between anchor and marker removal cannot
strand or replay the committed import. Normal
startup and journal appends remain blocked while that marker exists; rerun the same recovery command
with the exact configured path to resume an interrupted retirement without replaying committed
shares. Filesystem aliases of the active journal are rejected rather than given independent state.

Whole-file import manifests reject exact semantic replay of a complete source, but they are not a
per-share uniqueness key. Never import overlapping reviewed files such as `A` and later `A+B`, and
never combine previously imported records into a new source. Reconcile each source's provenance,
manifest hash and record count before import.

The supplied unit has `RestartPreventExitStatus=74`, while the independently stored latch blocks
every normal startup, including relay configurations. An inaccessible or uncertain state directory
also blocks startup. Notification delivery can still fail or time out; the fatal log, exit status
and latch are the authoritative signals. A later dual-target candidate failure upgrades an earlier
general shutdown to status 74, creates the latch and sends a distinct escalation alert containing
the affected candidates and exact latch path even when the first stop signal was already sent.
The fixed-name fatal latch remains deliberately small. It identifies the current incident, count,
pool set, failure category and any exact-share sidecar's path and expected SHA-256. Exact records are
streamed to that sidecar only after a `detailState=hash-pending` latch is force-flushed. The same
single streaming pass serializes each share, writes it and calculates the final SHA-256; only then
does Miningcore advance the incident and latch to `detailState=complete`. A serialization or
mid-write failure therefore still leaves startup blocked without first constructing or hashing the
whole payload. Every completed incident also receives an immutable
`.incident` metadata file rather than growing and rewriting all earlier incidents. New v3 incidents
carry a monotonic sequence and the exact previous-incident digest; the fixed latch anchors the chain
tip, complete expected count, and any legacy-v2 incident set present during the upgrade. Deleting,
reordering, or substituting an anchored incident therefore makes verification and startup fail.
Legacy incidents lost before the first v3 anchor cannot be reconstructed retroactively. Preserve and
reconcile every incident file and referenced sidecar. Never delete the fixed-name `.fatal` latch
manually: retained incidents without a complete acknowledgement anchor still block startup. After
database reconciliation, run `--verify-share-recovery-state` and then
`--acknowledge-share-recovery-state` with the service configuration. The acknowledgement command
re-verifies all evidence, durably publishes a new immutable `.acknowledged` chain anchor, and only
then removes the active latch. It preserves all incident metadata and sidecars, is safe to rerun
after interruption, and lets later incidents extend the acknowledged chain. Deleting or changing an
acknowledgement or any incident it covers makes startup fail closed. The verifier reads the small
`.fatal`, `.incident`, and `.acknowledged` metadata through the same restrictive,
identity-checked handle, with strict UTF-8, an exact 64-KiB raw-byte total limit, and a 16-KiB
per-line limit enforced while reading, and rejects mutation or path replacement while evidence is
being checked. Every later startup performs the complete sidecar count, framing and SHA-256
verification again, including after acknowledgement. A missing, truncated or replaced sidecar
therefore keeps startup blocked with status 74 rather than trusting metadata alone.

Acknowledging a prerelease v2-only incident set creates a durable v4 legacy-set anchor without
rewriting or deleting the original evidence. A later v3 incident extends from that preserved set.
Startup inspection, fatal-state publication and acknowledgement are serialized across Miningcore
processes with the path-scoped `.mutation.lock` file in the state directory. The lock file is
persistent state infrastructure, not incident evidence; leave it in place. If another service or
recovery command owns it, the operation fails closed and reports that ownership instead of racing.

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
