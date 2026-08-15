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
| `coinTemplates` | Additional operator-supplied coin-definition files |
| `cryptonightMaxThreads` / `equihashMaxThreads` | Native proof-validation concurrency limits |
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

Distributed sender/receiver roles have additional durability, security and database requirements;
use the dedicated [share-relay guide](share-relays.md) rather than copying an old relay example.

## Pool basics

Every enabled pool needs a unique `id`, a matching entry from `coins.json`, a pool wallet `address`,
one or more daemon RPC endpoints, at least one Stratum port in `ports`, and an appropriate
`paymentProcessing` section.

The configured Stratum `difficulty` is the initial fixed difficulty. A `varDiff` block allows the pool
to adjust it toward a target share interval. A miner can request a supported starting difficulty with
`d=VALUE` in its password.

## API listener isolation

`api.port` serves the public REST API and WebSocket notifications and defaults to `4000` when
omitted. Setting `api.adminPort` moves
`/api/admin` to a dedicated listener, while `api.metricsPort` moves `/metrics` to another dedicated
listener. A configured dedicated route is unavailable on the public port, and public routes are
unavailable on either dedicated port. Omitting an optional port retains the legacy shared-port
behavior for that route.

If `adminPort` is omitted, `/api/admin` remains reachable through the public listener. Configure the
reverse proxy to deny that path unless the existing admin IP whitelist and firewall are the intended
protection; publishing only `api.port` does not isolate a shared administrative route by itself.
If `metricsPort` is omitted, `/metrics` also remains on the public listener; a public reverse proxy
must deny that path unless exposing the metrics endpoint is intentional. Miningcore evaluates the
proxy connection's source address, so a same-host proxy normally appears as trusted loopback.

Every explicit API port must be unique and between 1 and 65535. An API listener conflicts with an
enabled local Stratum endpoint only when both use the same port and their bind addresses overlap;
the IPv4 wildcard overlaps every IPv4 address, while the dual-stack IPv6 wildcard overlaps both
IPv6 and IPv4 addresses. IPv4-mapped IPv6 addresses are treated as their equivalent IPv4 address.
Every configured port for an enabled internal Stratum listener must also be between 1 and 65535;
port `0` is rejected rather than creating an unpredictable ephemeral mining endpoint.
For clarity, `listenAddress: "*"` means all IPv4 interfaces (`0.0.0.0`); use `"::"` when a
dual-stack IPv6-any listener is required.

Enabled Stratum endpoints may reuse the same numeric port only when they bind distinct,
non-overlapping specific addresses. The default address is `127.0.0.1`; identical addresses,
IPv4-mapped equivalents, and any pairing covered by an IPv4 or dual-stack wildcard are rejected
before pools start. Startup reports every overlapping pair in one validation pass and identifies
both conflicting pools and their effective endpoints.

### Stratum listener reservation

Normal startup then creates, configures, binds and retains every enabled internal Stratum socket as
one cluster-scoped reservation phase. No pool initialization can announce `Online` until the whole
set has been reserved. If any endpoint is occupied, unavailable in the current host or container
network namespace, or otherwise rejected by the operating system, Miningcore releases every socket
acquired by that attempt and stops the complete cluster. The startup error identifies the pool,
effective endpoint, socket classification and native error. Miningcore hands those same retained
sockets to the Stratum accept loops; it does not probe, close and later rebind them. Reservation
calls `Bind` but deliberately defers `Listen` until that pool has completed initialization and enters
its accept path, so miners cannot accumulate in a connection backlog while daemon synchronization
or first-job setup is still pending. Reserved listeners are exclusive: Miningcore does not enable
`SO_REUSEADDR`, because two reuse-enabled sockets can bind the same endpoint before either calls
`Listen` on Linux and Windows does not provide deterministic ownership in that configuration.
Accepted sockets begin with abortive-close protection so an OOM kill, forced container stop,
process crash or ordinary host shutdown normally cannot strand the exclusive endpoint. Only a
genuine peer-initiated EOF disarms that protection and closes gracefully; bytes already written to
the network may then drain with FIN, but Miningcore does not drain its application send queue during
shutdown. Fail-stop, banned-client, pre-dispatch, malformed-request, TLS-handshake,
request-handler-failure, send-timeout and other independent-cancellation paths remain abortive. If
an unclean stop still leaves a
local `TIME_WAIT` entry, startup retries only `AddressAlreadyInUse` reservation failures with one
shared, bounded retry-delay budget totalling up to 90 seconds for the complete cluster reservation
attempt. Scheduled waits do not multiply with the number of endpoints. This is not a hard
wall-clock deadline: bind-call duration and scheduler overshoot are additional and ordinarily
negligible. Sockets acquired earlier in the attempt remain reserved but do not listen while a later
endpoint consumes that budget; this preserves the all-or-nothing ownership boundary. A genuinely
occupied port exhausts the shared delay allowance and then fails with the complete pool and socket
diagnostic; no partial cluster starts. Configure the service manager's startup timeout to exceed
the retry-delay budget, binding overhead and ordinary pool initialization time so it cannot
terminate Miningcore before the final diagnostic is emitted.

IPv4 broadcast and IPv4/IPv6 multicast addresses are rejected statically. IPv4 loopback addresses
throughout `127.0.0.0/8` and IPv4 link-local addresses in `169.254.0.0/16` remain valid configuration;
whether a specific address can be used on this host is decided authoritatively by the retained bind.
Miningcore also rejects subnet-directed broadcast identities positively identified from active local
IPv4 interface addresses and masks. Interface enumeration is not used to reject ordinary unicast
addresses, so containers, dynamic interfaces and failover addresses still rely on authoritative bind.
The active IPv4 subnet snapshot is captured once per validation or reservation pass and is used only
for positive directed-broadcast rejection, so one pass cannot classify ports from different host
interface snapshots.
For IPv6 link-local addresses, include the correct interface scope where the operating system
requires it. A missing or incorrect scope fails startup safely rather than leaving a partial pool set.
Dedicated listeners bind to the same `api.listenAddress` and use the same TLS certificate as the
public API, so retain the admin/metrics IP whitelists and restrict the ports with the host or network
firewall. Reverse proxies should publish only `api.port`; a local Prometheus service normally
scrapes `127.0.0.1:metricsPort`.

Permissive browser CORS remains enabled for public REST and WebSocket routes, but not for
`/api/admin` or `/metrics`. This applies on both dedicated and shared listeners and does not affect
Prometheus or other non-browser scrapers. Browser dashboards that previously read `/metrics`
directly across origins must use a deliberately secured same-origin telemetry service.

The admin IP whitelist is necessary but not sufficient. Every `/api/admin` request also requires the
bearer token provided through `MININGCORE_ADMIN_API_TOKEN`; there is intentionally no JSON property
for this secret. The value must contain exactly 64 hexadecimal characters. Missing or invalid token
configuration disables administrative requests while the pool continues running. See
[Administrative API security](admin-api-security.md) for systemd and Docker provisioning, TLS
requirements, request verbs and rotation.

Normal startup enforces the port range and conflict rules for an enabled API after configuration
loading. The JSON schema intentionally leaves listener-port values unconstrained. Stratum listener
checks apply only to enabled pools with internal Stratum enabled. If a disabled pool retains
internal Stratum endpoints, Miningcore logs that their validation was skipped and validates them
when the pool is enabled. Endpoint validation is also skipped for an enabled relay-only pool because
`enableInternalStratum: false` means its local `ports` are not bound; changing that setting to true
validates the endpoints before startup. Case-variant duplicate names remain errors in schema-bound
configuration objects. The intentionally free-form `payoutSchemeConfig` object is exempt because
its keys are consumed by payout-scheme implementations rather than bound to CLR properties.

### Recovery-mode configuration handling

`-rs` share recovery opens neither API nor Stratum sockets and does not initialize mining, hashing
or native solver runtimes. Miningcore prints a recovery-mode diagnostic and rebuilds the cluster
configuration from an explicit allowlist: `logging`, `persistence`, `pools`, `shareRecoveryFile`,
`shareRecoveryStateDirectory` and optional `coinTemplates`. Other top-level live settings are
discarded while the JSON is streamed, so malformed or duplicate API, statistics, relay, banning,
notification, NiceHash, memory, mining-concurrency and cluster-identity settings cannot block an
emergency import or recovery-state command. Recovery rebuilds `logging` from only the console
`level` and `enableConsoleColors` fields it consumes; malformed file-only settings are discarded,
while those two console fields remain strictly typed. If `logging` is absent, null or malformed as
a whole, Miningcore synthesizes the default informational, non-coloured console configuration so
the one-shot command remains visible. Other allowlisted settings retain strict duplicate, schema
and CLR-binding validation because recovery consumes them.

After strict duplicate and case-variant checks, recovery rebuilds every pool object from the only
pool fields it consumes: required `id` and optional string `coin` metadata. It discards every
live-only field, including cluster instance identity, enabled state, Stratum listeners, wallet and
daemon settings, payout and banning policy, reward recipients, timing values and extension data.
Empty `ports` and `daemons` placeholders satisfy the configuration schema without starting those
services. Damaged or stale
live-pool values therefore cannot block recovery, while ambiguous names and missing or malformed
pool identity remain errors. Pools may all be disabled during import. A non-empty pool collection
with unique, non-empty IDs and complete `persistence.postgres` settings remains mandatory. Those
IDs form a fail-closed import allowlist: every journal record must name one exactly. An unknown,
missing or mistyped record pool ID stops recovery before the pending marker, database transaction
or manifest registration so the operator can inspect the journal and add an intentional historical
pool explicitly.

Before import, Miningcore checks share-table partition coverage for every configured recovery pool
ID, including pools whose discarded live configuration had them disabled. After validating the
complete journal, it also checks the AuxPoW block-idempotency indexes when any record that would be
inserted uses a declared merged-mining block type. This evidence-driven check does not depend on the
discarded live `mergedMining` extension and runs before the import transaction or pending marker.

Optional template metadata is best-effort notification enrichment. Valid custom `coinTemplates`
paths are retained, non-string array entries are removed, and a malformed non-array value is
discarded. Miningcore attempts to assign loaded templates to every configured pool, including
disabled pools. Missing template files, missing pool coin metadata and undefined coins warn without
blocking the import; recovered block notifications may be skipped when no template is available.
Normal startup remains strict and rejects stale live-pool or template configuration.

### Containers and reverse proxies

Container host traffic does not appear as container loopback. To publish protected ports to host
loopback, use a user-defined bridge with a fixed gateway, add that gateway to both IP whitelists,
and verify the observed source address before relying on it:

```console
docker network create --driver bridge \
  --subnet 172.30.56.0/24 --gateway 172.30.56.1 miningcore
```

```json
"adminIpWhitelist": [ "172.30.56.1" ],
"metricsIpWhitelist": [ "172.30.56.1" ]
```

Choose another unused private subnet when this example overlaps an existing host or Docker network,
then use that network's fixed gateway in both whitelist entries.

Then start the container with `--network miningcore` and publish only the protected ports that the
host actually needs:

```console
-p 4000:4000 \
-p 127.0.0.1:4001:4001 \
-p 127.0.0.1:4002:4002
```

For a containerised Prometheus service, place both containers on a dedicated network and whitelist
Prometheus's predictable address instead. Do not assume Docker host traffic will appear as
`127.0.0.1` inside Miningcore.

See [API and monitoring](api.md#configuration) for the route matrix and post-upgrade checks.

## Coin definitions and native resources

Miningcore always loads the bundled `coins.json` beside the application. `coinTemplates` can add
operator-owned definition files after it. Keep custom files outside the versioned application
directory, validate every changed network and hashing field, and retest them after an upgrade.
Duplicate properties inside one file are rejected; an intentional definition in a later file can
replace one loaded earlier.

Native proof validation can consume substantial CPU and memory:

- `cryptonightMaxThreads` limits cluster-wide CryptoNight validation concurrency.
- `equihashMaxThreads` limits parallel Equihash solvers; each additional solver can add roughly 1 GiB
  to peak memory use.
- CryptoNote/RandomX pool extensions such as `randomXVmCount`, `randomXFlagsAdd` and full-memory flags
  are CPU- and coin-specific. Measure them on the production-class host before raising concurrency.
- Equihash-family pools can require a wallet-controlled `z-address` depending on the coin and payout
  path.
- Vertcoin/Verthash requires the correct `verthash.dat`; set `vertHashDataFile` in the pool extension
  data when the file is not in Miningcore's working directory.

Do not copy a setting from a different coin merely because it uses the same broad family. Read the
coin definition and daemon/wallet documentation together.

## Log files and rotation

`logging.level` accepts NLog's `trace`, `debug`, `info`/`information`, `warn`/`warning`, `error`,
`fatal` and `off`/`none` names without regard to case. Omit it or use an empty string for the
default `info` level. Miningcore rejects any other name during configuration validation, before
logging is configured.

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

## Share recovery storage

`shareRecoveryFile` is the write-through emergency journal used after PostgreSQL share persistence
exhausts its retries. Configure an absolute path so service working-directory changes cannot make
the journal difficult to locate. Miningcore logs the resolved path when the Share Recorder starts.
Restrict the file and its parent directory to the service account because share records are
financial accounting data.

### Ownership and path safety

Miningcore acquires an adjacent exclusive process-lifetime owner file before startup recovery checks
or import inspection. This intrinsic location prevents a different `shareRecoveryStateDirectory`
from creating an independent owner for the same journal. A second local recorder, merged-mining
relay submitter or recovery import using the same journal fails before pools start, even if it uses
different Stratum ports or reaches the parent through a directory symlink. Journal-file symlinks and
hard links are rejected; use one regular-file pathname.

Miningcore retains the physical parent-
directory identity and performs journal opens, temporary creation, publication, retirement, deletion
and Linux directory sync relative to that retained directory. Replacing a directory or retargeting
a configured parent symlink therefore cannot redirect an operation to a different directory and
fails closed before Miningcore continues. Such hostile namespace mutation can leave a temporary
file, archive, or unanchored journal in the originally retained physical directory. Preserve those
objects as forensic evidence and reconcile the journal, terminal anchor, import marker, and
PostgreSQL manifest before removing anything; do not assume that a failed operation made no durable
change.

The hidden owner must itself remain a single-name regular file: owner-file symlinks and hard links
are rejected. The owner is released only after a successful
final recorder drain; process-fatal shutdown retains it until the operating system closes the process
handle. Do not delete the `.miningcore-share-recovery-*.owner.lock` file beside `shareRecoveryFile`
while Miningcore is running.

### Atomic filesystem operations

On the supported Ubuntu 22.04 Linux target, Miningcore first uses
`renameat2(..., RENAME_NOREPLACE)` for atomic no-replacement publication and retirement. If libc does
not export that call, or the kernel/filesystem reports it unsupported, Miningcore falls back to
`linkat` followed by `unlinkat`. The link step still refuses an existing destination. A process or
machine loss between those fallback calls can leave both names linked to one inode; Miningcore's
single-link validation detects that state and fails closed for operator reconciliation. A filesystem
that supports neither primitive remains unsupported and fails the operation without overwriting an
existing entry. Retained-directory metadata is made durable with `fsync`.

Windows pins the resolved
physical directory and uses write-through child-file handles, which protects file contents and
prevents namespace redirection, but it does not claim the Linux-equivalent explicit parent-directory
`fsync` durability guarantee.

### Storage placement

For production, place the journal on a separately monitored filesystem or storage volume from
PostgreSQL data and Miningcore logs when possible. The objective is to preserve a writable failure
domain when database or log storage fills. If separate storage is unavailable, reserve capacity for
the journal and alert on both free bytes and free inodes well before exhaustion. Rotation of
Miningcore logs does not bound the journal or reserve space for it.

### Framed journal integrity

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

### Trusted appends and terminal anchors

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

### Bounded queues and overflow

A bounded 65,536-share persistence queue prevents an unlimited in-memory outage backlog. If it
fills, publication transfers the share to one bounded 1,024-share emergency channel and one journal
writer; filesystem work and waiting happen after the mining admission lock is released. The writer
drains up to 250 queued shares into one chained frame and one terminal-anchor update, bounding the
queue while avoiding a force-flush pair per share during sustained saturation. A local Stratum
response waits until the frame containing its share is durable. If both bounded capacities are
exhausted, or the emergency append fails, Miningcore admits no positive response and enters the
status-74 fatal path with the complete unresolved count and pool set.

The fatal transition first acquires the exclusive publication/response gate and waits for earlier
admissions to leave it; only then does it snapshot
the unresolved registry and write exact incident evidence. This quiescent ordering excludes shares
whose PostgreSQL commit became known before the gate closed and includes every still-unresolved
share that was published before closure.

### Volatile exposure

The normal 65,536-share queue is intentionally memory-resident for throughput. A positive Stratum
response proves local accounting-pipeline admission, but an abrupt process, kernel or machine loss
can still lose acknowledged entries that had not yet reached PostgreSQL. Treat 65,536 as the maximum
configured volatile exposure, not a crash-durability promise. Graceful shutdown drains active
Stratum request handlers before closing recorder intake, then accounts for every admitted queue item.
An unresponsive handler receives at most five seconds; expiry closes the global admission gate,
marks the process failed and lets Share Recorder retain its reserved persistence/recovery window.

### Graceful shutdown

Graceful shutdown first closes share intake, disposes the subscription and completes both writers.
The queue drain does not use the normal `BackgroundService` stopping token. Share Recorder limits
its own PostgreSQL drain to 20 seconds (or the remaining portion of the 45-second host budget,
whichever is shorter), then gives bounded transaction recovery and fatal-state handling at most 15
seconds before force-flushing the complete unresolved registry to the journal. The supplied
90-second systemd timeout provides a further margin for this
recovery work and other hosted services. Shutdown succeeds only after every
admitted share reaches PostgreSQL or the journal; failure of both destinations writes the same
status-74 latch for the full backlog.

### Legacy journals

Older journals retain the legacy newline-only guarantee for their original unframed prefix and v1
frames. The first v2 frame appended to an older journal anchors the complete normalised legacy
prefix, and all later frames are chained. A pure legacy/v1 source cannot retroactively prove that a
complete frame was never duplicated before the upgrade. The independent terminal anchor detects
offline deletion of complete v2 tail frames, while incident checksums and monitoring remain
necessary for legacy history. A newline alone is not proof that an older multi-record append completed.

### Dual-target failure and service state

If PostgreSQL and the journal are both unavailable, Miningcore immediately closes a coordinated
Stratum acceptance boundary. Every pool family publishes a validated share to the accounting
pipeline before admitting its positive response. Healthy publications and synchronous response-
queue admissions take concurrent read admissions, while fail-stop takes the exclusive transition;
unrelated pools are therefore not serialized by one global monitor. Queued responses are cancelled
directly when the gate closes.

Miningcore then writes an independent fatal latch under
`shareRecoveryStateDirectory/share-recovery-fatal`, attempts the distinct
`Fatal share-recovery fallback failure` administrative notification for up to five seconds, and
exits with status 74. The latch filename is the SHA-256 of the absolute configured journal path and
its content records both that path and hash. Under the supplied systemd unit the state directory is
`/var/lib/miningcore`; outside systemd, set `shareRecoveryStateDirectory` explicitly when the
platform application-data default is unsuitable. Keep this state on service-owned storage that is
independent of the journal's expected failure domain. Container deployments should mount that state
directory on persistent storage so replacing the container cannot discard an unreconciled latch.

#### State-file validation

The sibling `share-recovery-terminal` and `share-recovery-import` directories are equally
authoritative. All three safety-state subdirectories are pre-created during startup, with every new
directory entry parent-synchronised on Linux before mining is accepted. Do not delete or edit their
path-hashed files independently. Miningcore proves that a terminal or import marker is absent only
after successfully enumerating its exact state directory. Exact state and alias inspection uses
atomic no-follow handles on supported Linux and Windows hosts. A directory, symbolic link,
unsupported file type, malformed marker, disappearing entry, or inaccessible/uncertain directory blocks startup
and the first fallback append instead of being treated as absence.

#### Interrupted import state

Recovery writes a pending import marker before opening its PostgreSQL transaction. After commit it
advances the marker through
`Committed`, `ArchiveDurable`, `AnchorRetirementAuthorised`, and `AnchorRetired`, then removes it
only after reopening and fully revalidating the retained source, atomically archiving and
directory-syncing it, revalidating the same file object, and retiring the matching anchor. Each
phase retains the validated terminal sequence and digest. Anchor absence is valid on resume only
after durable retirement authorisation, so interruption between anchor and marker removal cannot
strand or replay the committed import. Normal
startup and journal appends remain blocked while that marker exists; rerun the same recovery command
with the exact configured path to resume an interrupted retirement without replaying committed
shares. Filesystem aliases of the active journal are rejected rather than given independent state.

### Import overlap safety

Whole-file import manifests reject exact semantic replay of a complete source, but they are not a
per-share uniqueness key. Never import overlapping reviewed files such as `A` and later `A+B`, and
never combine previously imported records into a new source. Reconcile each source's provenance,
manifest hash and record count before import.

### Fatal incident evidence and acknowledgement

The supplied unit has `RestartPreventExitStatus=74`, while the independently stored latch blocks
every normal startup, including relay configurations. An inaccessible or uncertain state directory
also blocks startup. Notification delivery can still fail or time out; the fatal log, exit status
and latch are the authoritative signals. A later dual-target candidate failure upgrades an earlier
general shutdown to status 74, creates the latch and sends a distinct escalation alert containing
the affected candidates and exact latch path even when the first stop signal was already sent.

#### Exact-share evidence

The fixed-name fatal latch remains deliberately small. It identifies the current incident, count,
pool set, failure category and any exact-share sidecar's path and expected SHA-256. Exact records are
streamed to that sidecar only after a `detailState=hash-pending` latch is force-flushed. The same
single streaming pass serializes each share, writes it and calculates the final SHA-256; only then
does Miningcore advance the incident and latch to `detailState=complete`. A serialization or
mid-write failure therefore still leaves startup blocked without first constructing or hashing the
whole payload.

#### Incident chain

Every completed incident receives an immutable `.incident` metadata file rather than growing and
rewriting all earlier incidents. New v3 incidents
carry a monotonic sequence and the exact previous-incident digest; the fixed latch anchors the chain
tip, complete expected count, and any legacy-v2 incident set present during the upgrade. Deleting,
reordering, or substituting an anchored incident therefore makes verification and startup fail.
Legacy incidents lost before the first v3 anchor cannot be reconstructed retroactively. Preserve and
reconcile every incident file and referenced sidecar.

#### Operator acknowledgement

Never delete the fixed-name `.fatal` latch manually: retained incidents without a complete
acknowledgement anchor still block startup. After database reconciliation:

1. Run `--verify-share-recovery-state` with the service configuration.
2. Run `--acknowledge-share-recovery-state` with the same configuration.

The acknowledgement command re-verifies all evidence, durably publishes a new immutable
`.acknowledged` chain anchor, and only then removes the active latch. It preserves all incident
metadata and sidecars, is safe to rerun after interruption, and lets later incidents extend the
acknowledged chain. Deleting or changing an acknowledgement or any incident it covers makes startup
fail closed.

The verifier reads the small `.fatal`, `.incident`, and `.acknowledged` metadata through the same
restrictive, identity-checked handle. It enforces strict UTF-8, a 64-KiB total raw-byte limit and a
16-KiB per-line limit while reading, and rejects mutation or path replacement during verification.
Every later startup performs the complete sidecar count, framing and SHA-256 verification again,
including after acknowledgement. A missing, truncated or replaced sidecar therefore keeps startup
blocked with status 74 rather than trusting metadata alone.

### Legacy incident acknowledgement and locks

Acknowledging a prerelease v2-only incident set creates a durable v4 legacy-set anchor without
rewriting or deleting the original evidence. A later v3 incident extends from that preserved set.
Startup inspection and fatal-state publication are serialized across Miningcore processes with the
path-scoped `.mutation.lock` file in the state directory. The lock file is persistent state
infrastructure, not incident evidence; leave it in place. The acknowledgement command additionally
acquires the journal's native process-lifetime owner before taking the mutation lock. It therefore
cannot mutate evidence while a recorder, merged-mining submitter or importer owns the journal, even
when .NET managed file locking is disabled on Unix. If another service or recovery command owns
either boundary, the operation fails closed and reports that ownership instead of racing.

### Operator response

The normal `Share Recorder Policy Fallback` event confirms that one fallback batch was force-flushed;
it is not proof that every share throughout an outage reached the journal. Review the complete
incident log and follow the [disk-exhaustion recovery runbook](database.md#recover-after-disk-exhaustion)
before importing or restarting.

## Validate changes safely

1. Keep a known-good copy of the current configuration outside the repository.
2. Edit one logical area at a time.
3. Start Miningcore in the foreground and read every startup warning.
4. Check `/api/health-check` and `/api/pools`.
5. Connect a test miner at low risk before moving production traffic.
6. For merged mining, repeat the documented regtest and schema preflight when topology changes.
