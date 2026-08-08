# Installing a prebuilt Miningcore release

GitHub Releases provide a tested, framework-dependent build for **Ubuntu 22.04 x64** and a container
image built from that same archive. The archive saves compilation time, but it still requires the
.NET 10 ASP.NET Core runtime and Miningcore's native runtime libraries. Windows and other Linux
distributions are not represented as binary-compatible by this package; use the source-build guide
in the root README for those environments.

> **Runtime requirement:** install a supported, serviced .NET 10 ASP.NET Core runtime from the
> documented Ubuntu package source and keep it updated with normal security maintenance.

TLS-enabled Stratum endpoints rely on the host security policy and accept TLS 1.2 or TLS 1.3 on
supported, patched hosts. Legacy miners limited to TLS 1.0 or TLS 1.1 must be upgraded or replaced.

If this replaces an existing .NET 6 deployment, first follow the dedicated
[.NET 6 to .NET 10 migration guide](dotnet-6-to-10-migration.md). Do not treat the clean-install
commands below as an instruction to overwrite a live configuration or database.

New installations can continue at [Choose a version](#choose-a-version). Existing operators should
first read [Logging and disk recovery](#logging-and-disk-recovery) and
[Payout and WebSocket compatibility](#payout-and-websocket-compatibility) for behavior changes that
may require monitoring, migration or front-end work.

## Choose a version

Versions containing a suffix such as `v0.1.0-rc.1` are release candidates. Test them before relying
on them for real funds. A version without a suffix, such as `v0.1.0`, is a stable release and updates
the `latest` container tag.

Open the [releases page](https://github.com/NINJAK1DD/miningcore/releases), choose a version, and
download these two files:

- `miningcore-VERSION-linux-x64-ubuntu-22.04.tar.gz`
- `SHA256SUMS`

The examples below use the current `v0.1.0-rc.8`. Substitute the version you selected.

```console
export MININGCORE_VERSION=v0.1.0-rc.8
curl -fLO "https://github.com/NINJAK1DD/miningcore/releases/download/${MININGCORE_VERSION}/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-22.04.tar.gz"
curl -fLO "https://github.com/NINJAK1DD/miningcore/releases/download/${MININGCORE_VERSION}/SHA256SUMS"
sha256sum --check SHA256SUMS
```

`SHA256SUMS` covers the release archive. GitHub also publishes build provenance for the archive. If
the [GitHub CLI](https://cli.github.com/) is installed, verify it with:

```console
gh attestation verify "miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-22.04.tar.gz" \
  --repo NINJAK1DD/miningcore
```

After extraction, compare the packaged metadata with the binary before changing the live service:

```console
cat "/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-22.04/BUILD-INFO"
LD_LIBRARY_PATH="/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-22.04" \
  "/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-22.04/Miningcore" --version
```

`BUILD-INFO` must name the selected release and source commit. Releases published after the
version-reporting validation was introduced must report the same semantic version (without the
tag's leading `v`) and full commit SHA. Older releases, including `v0.1.0-rc.2`, can show the legacy
`0.1.0.0-BRANCH` format; match their full embedded SHA to `BUILD-INFO` instead. A branch label such
as `dev` is not sufficient release provenance by itself.

## Install runtime dependencies

Enable Canonical's supported .NET backports PPA, then install the framework and native libraries:

```console
sudo apt-get update
sudo apt-get install -y software-properties-common
sudo add-apt-repository -y ppa:dotnet/backports
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-10.0 libgmp10 libsodium-dev libzmq3-dev
```

## Install the archive

Create a dedicated service account, unpack the versioned directory, and point a stable symlink at
it:

```console
id -u miningcore >/dev/null 2>&1 || \
  sudo useradd --system --home /var/lib/miningcore --shell /usr/sbin/nologin miningcore
sudo mkdir -p /opt /etc/miningcore /var/lib/miningcore /var/log/miningcore
sudo tar -xzf "miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-22.04.tar.gz" -C /opt
sudo ln -sfn "/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-22.04" /opt/miningcore
sudo cp /opt/miningcore/config.example.json /etc/miningcore/config.json
sudo chown -R miningcore:miningcore /var/lib/miningcore /var/log/miningcore
sudo chown root:miningcore /etc/miningcore
sudo chown root:miningcore /etc/miningcore/config.json
sudo chmod 0750 /etc/miningcore
sudo chmod 0640 /etc/miningcore/config.json
```

Edit `/etc/miningcore/config.json`. Replace every `CHANGE_ME` value and use absolute writable paths
for service state:

```json
"logging": {
  "logBaseDirectory": "/var/log/miningcore"
},
"shareRecoveryFile": "/var/lib/miningcore/recovered-shares.txt"
```

Keep RPC, wallet, database and notification credentials out of the installation directory and out
of source control.

## Prepare PostgreSQL

For a new database, use the packaged schema:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f /opt/miningcore/migrations/createdb.sql
```

For an existing database, stop all Miningcore writers and payout managers, take a tested backup,
and apply the migrations required by the release before starting the new binary. Read the packaged
`docs/database.md`; do not blindly rerun `createdb.sql` over an existing database.

## Install the systemd service

The supplied unit directly supervises Miningcore, runs it as the unprivileged `miningcore` user, and
allows 90 seconds for the application's bounded clean shutdown and durable recovery-state margin:

```console
sudo cp /opt/miningcore/systemd/miningcore.service /etc/systemd/system/miningcore.service
sudo mkdir -p /etc/miningcore
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env >/dev/null
unset token
sudo chown root:root /etc/miningcore/miningcore.env
sudo chmod 0600 /etc/miningcore/miningcore.env
sudo systemctl daemon-reload
sudo systemctl enable --now miningcore
sudo systemctl status miningcore
sudo journalctl -u miningcore -f
```

The unit expects `/opt/miningcore` and `/etc/miningcore/config.json`. It creates writable
`/var/lib/miningcore` and `/var/log/miningcore` directories through systemd. If startup fails, read
the complete journal before restarting; repeated restarts do not repair missing migrations or bad
daemon credentials. For a local database, stop Miningcore before PostgreSQL so the payout manager
can release its durable owner cleanly. If ownership remains after a database-session or process
loss, use the guarded [payout-manager recovery runbook](database.md#recover-payout-manager-ownership-safely).

## Upgrade or roll back

For a major-runtime upgrade from .NET 6, use the more detailed
[.NET 6 to .NET 10 migration guide](dotnet-6-to-10-migration.md). The sequence below is the shorter
procedure for routine upgrades after the deployment layout and runtime are already suitable.

1. Back up PostgreSQL, the configuration, and recovery journal.
2. Download and verify the new archive.
3. Stop Miningcore and confirm no other payout manager owns the same pools/database.
4. Apply release-specific database migrations.
5. Extract the new version and change `/opt/miningcore` with `ln -sfn`.
6. Start Miningcore and inspect its startup, daemon-sync, recorder, and payout-manager logs.

If application rollback is necessary, stop the service and repoint the symlink to the previous
directory. Database migrations may not be reversible; restore the matching backup when the release
notes or migration guide requires it. Never run old and new payout managers concurrently.

## Use the GitHub Container Registry image

Release images are published for Linux AMD64 at
`ghcr.io/ninjak1dd/miningcore`. Pin a specific version in production rather than `latest`:

```console
export MININGCORE_VERSION=v0.1.0-rc.8  # Replace with the release you selected.
sudo mkdir -p /etc/miningcore /var/lib/miningcore
sudo curl -fL \
  "https://raw.githubusercontent.com/NINJAK1DD/miningcore/${MININGCORE_VERSION}/config.example.json" \
  -o /etc/miningcore/config.json
sudo chown root:10001 /etc/miningcore/config.json
sudo chmod 0640 /etc/miningcore/config.json
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env >/dev/null
unset token
sudo chown root:root /etc/miningcore/miningcore.env
sudo chmod 0600 /etc/miningcore/miningcore.env
sudo chown 10001:10001 /var/lib/miningcore
sudo docker pull "ghcr.io/ninjak1dd/miningcore:${MININGCORE_VERSION}"
sudo docker run -d \
  --name miningcore \
  --restart unless-stopped \
  --env-file /etc/miningcore/miningcore.env \
  -p 4000:4000 \
  -p 3032:3032 \
  -v /etc/miningcore/config.json:/etc/miningcore/config.json:ro \
  -v /var/lib/miningcore:/var/lib/miningcore \
  "ghcr.io/ninjak1dd/miningcore:${MININGCORE_VERSION}"
```

Publish every API and Stratum port used by your configuration. The container runs as fixed non-root
UID/GID `10001`; its configuration and state mounts must be readable/writable by that identity. Its
`127.0.0.1` is the container itself, so database and daemon endpoints must be reachable
from the container network.

## Operational and compatibility changes

Review these release-specific changes before upgrading an existing pool. New installations can
return to them after completing the deployment steps above.

### Security: administrative API bearer authentication and safe verbs

Every `/api/admin` request now requires both the existing source-IP whitelist and a bearer token
provided through the `MININGCORE_ADMIN_API_TOKEN` environment variable. The secret is deliberately
not accepted in `config.json`. It must contain exactly 64 hexadecimal characters. If it is missing
or malformed, Miningcore keeps mining and public API services online but returns `503 Service
Unavailable` for administrative routes. Admin routes emit no CORS headers, and operators must never
expose the token to a browser or public WebUI.

Before upgrading, generate and provision a token using the
[administrative API security guide](admin-api-security.md). Existing administrative clients must add
the `Authorization: Bearer TOKEN` header. Logging-level and payment-processing mutations, and the
admin miner-settings mutation, now use `PUT` rather than `GET` or `POST`; force-GC remains `POST`.
Read-only administrative `GET` routes also require authentication. The former public
`POST /api/pools/{poolId}/miners/{address}/settings` endpoint has been removed because knowledge of a
recent mining IP address was not adequate authorization. This is a breaking but necessary security
change for admin scripts and front ends.

Two read-only responses also become stricter. An administrative balance lookup for an unknown pool
now returns `404 Not Found` instead of a misleading zero balance, and the public `/api/help`
catalogue no longer lists administrative routes. Update scripts that depended on either legacy
behaviour before deploying.

### Security: administrative API whitelist enforcement

This release fixes an administrative API whitelist bypass that affected shared public/API
listeners. ASP.NET Core routes paths case-insensitively, but the previous whitelist check compared
path prefixes case-sensitively. A differently cased path such as `/API/ADMIN/...` could therefore
reach an administrative controller without passing through the configured admin IP whitelist.
Protected-path matching is now case-insensitive and path-segment bounded. It also fails closed when
the remote address is unavailable and correctly treats IPv4-mapped IPv6 addresses as their IPv4
equivalent. Operators who keep `/api/admin` on the shared public listener should treat this as a
security fix and update promptly.

### Dedicated admin and metrics listeners

`api.adminPort` and `api.metricsPort` now create real, route-isolated listeners. When configured,
`/api/admin` and `/metrics` are no longer served on the public `api.port`; public REST and WebSocket
routes are not served on either dedicated port. The existing IP whitelists continue to apply.

Before upgrading a configuration that sets these ports:

1. Permit the required trusted sources to reach the dedicated ports through the firewall.
2. Point Prometheus at `http://127.0.0.1:4002/metrics` or the configured equivalent. For the
   standard ports, change `http://127.0.0.1:4000/metrics` to `http://127.0.0.1:4002/metrics`.
3. Keep reverse proxies and public clients on `api.port` only.
4. Publish the extra ports explicitly for a container deployment.
5. Verify firewall, container and reverse-proxy mappings before restarting, then confirm protected
   routes return 404 on the public port.

`api.port` defaults to `4000` when omitted. Omitting either optional port keeps that route on the
public listener for backwards compatibility. If `adminPort` is omitted, explicitly deny
`/api/admin` at the reverse proxy unless the admin whitelist and firewall are the intended
protection. If `metricsPort` is omitted, likewise deny `/metrics` unless public metric exposure is
intentional. A same-host reverse proxy normally reaches Miningcore from trusted loopback, so the
application whitelist alone does not block a route forwarded by that proxy. Explicit API ports must
be unique and in the range 1–65535. Enabled internal Stratum ports must also be in that range;
port `0` is now rejected instead of creating an unpredictable ephemeral mining endpoint.
TLS-enabled deployments use the same configured certificate on every listener. An API listener
that uses the same port and an overlapping bind address as an enabled local Stratum endpoint now
stops startup with the conflicting port identified; different specific bind addresses may reuse a
port. See
[API listener isolation](configuration.md#api-listener-isolation).

Listener-only validation is skipped during `-rs` share recovery because that mode opens no API or
Stratum sockets. Recovery stream-discards every top-level `api` case variant before strict duplicate
and schema validation. After strict duplicate and case-variant checks, it replaces each pool's unused
`ports` subtree before schema validation and typed dictionary binding, then skips the remaining
Stratum port, address and TLS certificate checks. Exact duplicates outside the unused API subtree and
case-variant ambiguities remain errors. The configuration schema therefore defers listener range and
conflict enforcement to normal startup so an invalid, duplicated or stale listener setting cannot
block a durable-share import or recovery-state command.

Recovery also sanitizes optional `coinTemplates` metadata before schema validation. Valid custom
template paths are retained, while non-string entries or a malformed non-array value cannot block
share import, verification or acknowledgement. Normal startup continues to reject those values.

The regenerated configuration schema also corrects previously omitted boolean fields
(`banning.banOnLoginFailure`, `logging.gpdrCompliant`, `pools[].enableAsicBoost` and
`persistence.postgres.enableLegacyTimestamps`) and requires non-null strings in `coinTemplates` and
all address-whitelist arrays.

### Auxiliary-template RPC observability

Litecoin-Dogecoin merged mining now distinguishes a configured `createauxblock` timeout from host
or shutdown cancellation. Timeout logs name the configured deadline instead of reporting the
generic `Cancelled` transport text. Failed, timed-out and cancelled attempts are included in a new
seconds-based auxiliary RPC histogram with bounded phase and outcome labels. Its `_count` series is
the attempt counter. Separate metrics report entries into degraded cached-template operation,
whether a usable merged-mining job is installed and whether the parent pool is currently using
cached auxiliary data. Every series identifies both the Litecoin parent and Dogecoin auxiliary
pool. RPC series also separate ten-second startup
synchronization from recurring template refreshes, so two parents that share one auxiliary pool
cannot overwrite each other's state or combine different timeout policies.
The histogram has ten bounded label sets per configured parent/auxiliary pair (two phases by five
outcomes); each label set exports the configured buckets plus `+Inf`, `_sum` and `_count`.

Miningcore continues Litecoin mining with the last valid Dogecoin template during a temporary
auxiliary refresh failure. A successful refresh clears the degraded gauge only after its template
is installed in a merged-mining job, or when it reconfirms the auxiliary identity already installed.
Availability remains zero between daemon synchronization and installation of the first usable
merged-mining job, even when the daemon returned a parseable template. Deadline enforcement is now
deterministic: a response that arrives after timeout or shutdown already won is discarded rather
than allowed to override that result. This can expose a narrow increase in entries into degraded
cached-template operation at the configured deadline boundary. Review the troubleshooting guidance
before changing `auxiliaryTemplatePollTimeoutMs`, because a longer deadline can delay a fresh parent
job.

### Logging and disk recovery

Miningcore now rotates every configured NLog file natively before a write would grow it beyond
512 MiB and retains four archives per file target. Remove legacy Miningcore `logrotate` rules that
use `copytruncate`; combining both mechanisms can create sparse files, while restarting the service
from `postrotate` disconnects miners. See
[Log files and rotation](configuration.md#log-files-and-rotation) for capacity planning.

The database guide now includes a guarded [disk-exhaustion recovery runbook](database.md#recover-after-disk-exhaustion).
It restores storage, PostgreSQL and coin daemons in dependency order before Miningcore and links to
the existing payout-ownership reconciliation procedure for an unclean database-session loss.

#### Recovery journal integrity

Recovery-journal appends now roll back a partial write to the previous file length, force-flush the
rollback, and refuse to extend an incomplete line or framed batch. First creation uses a
force-flushed temporary file, atomic rename and Linux parent-directory synchronisation.

The framed journal adds the following integrity checks:

- A first-byte format marker and chained v2 batch trailers record the sequence, previous frame,
  expected count, record SHA-256 and deterministic frame digest.
- Miningcore streams and verifies the chain at startup, on first fallback entry and before import.
- Later appends verify the cached file identity and length, then hash only the new frame.
- Every forced append commits an independent terminal sequence/digest anchor, detecting removal of a
  complete final frame.
- Readers limit individual recovery lines to 1,048,576 characters and normalise frame-content line
  endings to `\n`.

The independent anchor protects newly committed terminal frames. Incident checksums remain necessary
for legacy history.

#### Queue overflow and graceful shutdown

A bounded persistence queue transfers overflow to one bounded emergency journal writer outside the
mining admission lock. It does not accept unlimited memory or blocked-caller backlogs. The emergency
writer drains up to 250 shares into one force-flushed chained frame and anchor update; each affected
Stratum response waits for its containing batch.

Graceful stop drains acknowledged shares independently of hosted-service cancellation. It allows up
to 20 seconds for PostgreSQL, reserves 15 seconds for transaction recovery and fatal handling, then
uses the remaining service-manager window to journal the unresolved registry. The supplied systemd
unit has a 90-second stop timeout.

#### Fatal accounting failure

If PostgreSQL and the recovery journal both fail, Miningcore closes a coordinated share-acceptance
boundary. It stops new admissions, drains earlier publication and response admissions, captures the
quiescent unresolved registry, and cancels queued responses. Miningcore then:

1. Writes a persistent, hashed fatal latch in an independent service-owned state directory.
2. Attempts a bounded critical administrative notification.
3. Exits with dedicated status 74 instead of continuing without durable accounting.

Candidate persistence uses the same mandatory latch and direct alert path. The supplied systemd unit
does not restart status 74, and every normal startup—including relay nodes—remains blocked until the
incident is reconciled and acknowledged. A later dual-target candidate loss upgrades an earlier
general shutdown to status 74 and records a distinct incident.

The fixed latch is force-flushed in a hash-pending state before exact shares are streamed once into
an incrementally hashed sidecar. Serialization and incomplete-sidecar failures therefore still block
restart. The read-only `--verify-share-recovery-state` command validates incident metadata, sidecar
hashes and bounded records without modifying evidence. Memory exhaustion stops verification rather
than attempting to continue.

State-directory uncertainty also fails closed with status 74. Miningcore accepts missing terminal or
import state only after exact directory enumeration; directories, symbolic links, unsupported
entries, malformed content and inaccessible state are rejected.

#### Recovery path ownership and filesystem safety

Configure `shareRecoveryFile` as an absolute path on separately monitored or reserved storage where
possible. The database runbook explains evidence preservation and manifested import verification.

Local recording, merged-mining relay submission and recovery import acquire the same adjacent,
process-lifetime ownership lock before inspecting state and retain it through final shutdown
journalling. Its identity does not depend on `shareRecoveryStateDirectory`. Linux uses a native
exclusive lock; Windows retains an exclusive handle. A second process using the same recovery path
fails before pools start, regardless of its Stratum configuration.

Miningcore retains the physical parent directory for journal creation, append, validation, import
and retirement. A stable parent symlink is supported, but later replacement or retargeting fails
closed. Journal and owner-file symlinks, hard links and non-regular objects are rejected without
blocking on FIFOs. The acknowledgement command acquires the same native owner before changing fatal
evidence.

On supported Ubuntu 22.04 hosts, no-replacement publication uses
`renameat2(..., RENAME_NOREPLACE)` plus retained-directory `fsync`. Unsupported libc, kernel or
filesystem responses use a no-replace `linkat`/`unlinkat` fallback. A crash between those calls can
leave two names for one inode; single-link checks reject that state. Filesystems supporting neither
method are unsupported rather than allowed to replace evidence.

Windows pins the physical directory and uses write-through child handles, but does not claim an
equivalent explicit directory-metadata `fsync`. A hostile parent retarget can leave forensic files in
the retained directory after an operation fails closed; preserve and reconcile them rather than
deleting them as routine cleanup.

#### Interrupted recovery import

Recovery import uses a durable, multi-phase source-retirement marker. Startup and journal appends
stay blocked until Miningcore has:

1. Revalidated the source chain, anchor, semantic hash, record count and file identity.
2. Renamed and synchronised the committed source archive.
3. Recorded archive durability and anchor-retirement authorisation.
4. Retired the anchor while retaining its validated terminal sequence and digest.

Rerunning the same recovery command resumes this sequence without changing the manifest identity or
replaying records. Miningcore rechecks the same non-writable file after rename, rejects aliases of
the configured source, and validates the retained directory and marker at destructive boundaries.
Do not import overlapping reviewed files: manifests identify whole sources, not individual shares.

Prometheus now exports current depth, process-lifetime high-water mark and configured capacity for
both the primary share-persistence queue and emergency recovery-journal queue. The fixed `queue`
labels are `primary` and `emergency_journal`; operators can alert on saturation trends before the
emergency path or fail-stop boundary is reached. Admission and removal use exact serialized
occupancy accounting under concurrent producers, and an overflow counter records every rejected
write. Relay-only nodes omit these local-recorder series instead of reporting nonexistent queues as
healthy and empty.

Fatal incident completion is resumable across the durable boundary between publishing a completed
incident/sidecar and replacing its earlier hash-pending latch. Verification reports that exact state
as recoverable but still startup-blocking; startup or acknowledgement revalidates the immutable
fields, initial-latch digest, complete sidecar and chain tip under the mutation lock before publishing
the completed latch. Any mismatch remains startup-blocking evidence.

#### PostgreSQL transaction outcome safety

Unexpected mapper, connection, transaction or repository failures now quiesce mining, force-flush
the unresolved registry to the recovery journal and stop with a general failure. If the journal also
fails, status 74 and the fatal latch remain authoritative.

The share-persistence PostgreSQL transaction lifecycle is cancellation-aware and bounded through
open, begin, repository commands, commit, rollback and cleanup. Transaction and connection disposal
run as one ordered background sequence under a four-second aggregate wait because ADO.NET disposal
does not accept cancellation. Other API, statistics and payout `RunTx` callers retain synchronous,
ordered disposal.

If transaction disposal consumes the bound, connection disposal waits for it. Cleanup that finishes
later logs its outcome, resource stage and elapsed time. Once the commit outcome is known, cleanup
can add evidence but cannot change that classification:

- Cleanup failure after a known commit removes that batch from replayable state.
- Cleanup failure while commit is uncertain remains secondary evidence.
- A PostgreSQL error with a SQLSTATE proves that `COMMIT` was rejected and the batch is replayable.
- Transport errors, timeouts, cancellation after commit entry and unknown provider failures remain
  outcome-uncertain.

An unproven commit is never copied into the importable journal. Its exact share JSON is written to
the sidecar referenced by the status-74 latch for manual reconciliation.

Active Stratum dispatch tasks and in-flight requests receive a five-second bounded drain before
Share Recorder intake closes. If that expires, Miningcore closes admission and returns a non-zero
stop without consuming the recorder's reserved shutdown window.

#### Incident evidence and acknowledgement

Fatal, terminal and import state subdirectories are parent-synchronised on first creation. State and
alias inspection uses atomic, no-follow regular-file handles on Linux and Windows.

Fatal incidents form a sequence and previous-digest chain anchored by the fixed latch's tip and
expected count. The first v3 incident also anchors retained legacy-v2 incidents. After database
reconciliation, `--acknowledge-share-recovery-state` re-verifies all evidence, publishes an immutable
`.acknowledged` anchor, and removes only the active latch.

Manual latch deletion does not unblock startup. Acknowledgement resumes safely after interruption,
later incidents extend the acknowledged tip, and changed or missing evidence fails closed. Metadata
verification enforces strict UTF-8, a 64-KiB total raw-byte limit, bounded lines and stable path and
handle identity. Every startup rechecks acknowledged sidecars, hashes and record counts. Prerelease
v2-only incident sets can be preserved with a v4 legacy-set anchor.

A persistent, path-scoped mutation lock serializes startup inspection, fatal publication and
acknowledgement across processes. Before destructive source rename or anchor removal, recovery import
also re-confirms the exact PostgreSQL manifest through a fresh connection.

These local-recorder guarantees do not turn `shareRelay` into an acknowledged transport. A relay
sender's positive response proves only local in-memory relay-queue admission, not remote receipt or
PostgreSQL persistence. Also, up to the normal 65,536-share local recorder queue remains volatile
during abrupt process or machine loss; the bound limits exposure but does not provide power-loss
durability.

### Payout and WebSocket compatibility

This release changes Bitcoin-family payout accounting from rounding to truncation at the configured
`payoutDecimalPlaces`. The truncated wallet request is now also the payment-history amount and miner
balance deduction; any residual remains on the balance for a later payout. Review the
[configuration guidance](configuration.md#bitcoin-family-payout-precision), particularly when a
template relies on the four-decimal fallback.

The public WebSocket `payment` event is also revised. It adds `outcome`, `submittedAmount`,
`precisionAdjustment` and safe accepted/failed/uncertain/not-attempted aggregate counts and amounts.
It no longer exposes `error` or recipient-level reconciliation because those fields can reveal wallet
errors, addresses and transaction mappings. Update front ends that consumed the old `error` field
before deploying this release; see the [payment event contract](api.md#payment-event-contract).

Uncertain payout notification ownership and partial-batch reconciliation now apply across supported
coin families, including paged and per-recipient wallet APIs. Known persisted, rejected, in-flight
and untouched recipients remain distinct when a later submission becomes uncertain. Administrative
amounts use exact invariant decimal formatting with insignificant trailing zeroes removed, and
duplicate transaction IDs returned by separate per-recipient submissions fail closed.

Kaspa multi-transaction payouts additionally require a complete ordered identity set, persist the
final recipient-facing transaction as canonical, and retain every prerequisite ID for notification
and reconciliation. Kaspa success events preserve the existing flat `txIds` list and add an optional
`recipientTransactionChains` mapping with each recipient address, canonical ID and ordered chain.
Equihash and Handshake payout wallets unlocked by Miningcore are relocked in bounded cleanup even
when payout processing fails or the host is shutting down. Handshake persists a returned transaction
before relock cleanup; relock errors raise a separate administrative alert without replacing the
financial outcome. Handshake now requires successful wallet discovery or selection before `sendmany`.

Handshake and Equihash treat cancellation during `walletpassphrase` as ordinary pre-submission
shutdown and conservatively attempt bounded relock when the unlock result is unknown.

### Coin definition accuracy

The bundled definitions now select StakeCubeCoin's current SCCPow implementation instead of a
later duplicate legacy X11 entry, and Zetacoin's hybrid PoW/PoS definition now uses its current
Scrypt proof-of-work algorithm. Duplicate JSON properties at any level within one coin-definition
file—including coin identifiers, nested hasher settings, and network parameters—are rejected at
startup instead of silently allowing the final value to override an earlier one. This is a
deliberate fail-closed compatibility change; explicit redefinitions across separately loaded files
remain supported.

The stale HelpTheHomeless X16R definition has been removed because the maintained chain uses X25X,
which is not included in the packaged native runtimes. DigiByte Odocrypt is likewise not advertised:
Miningcore's historical Odocrypt implementation was removed as non-working. MeowCoin's existing
MeowPow definition remains valid; its newer Scrypt mode is AuxPoW-only and requires generalized
merged-mining support before it can be offered as a Miningcore template.

## Maintainer release procedure

The release workflow accepts SemVer tags reachable from `dev`, for example `v0.1.0-rc.9` or
`v0.1.0`. It first builds and smoke-tests the source `Dockerfile`, then rebuilds on Ubuntu 22.04,
runs the complete PostgreSQL-backed and ZeroMQ test suite, validates native runtime links,
checks that the binary reports the release version and source commit, packages the result,
smoke-tests the packaged image, and publishes both artifacts with provenance.
The tagged build injects the validated tag and commit as assembly metadata because development
branches intentionally retain GitVersion's prerelease calculation; the runtime check requires an
exact match before packaging can begin.
This additional source-container gate makes release runs longer but catches Dockerfile-only build
failures before publication. Prefer a signed annotated tag:

```console
git switch dev
git pull --ff-only origin dev
NEXT_VERSION=v0.1.0-rc.9  # Replace with the next unused SemVer version.
git tag -s "$NEXT_VERSION" -m "Miningcore $NEXT_VERSION"
git push origin "$NEXT_VERSION"
```

If signed tags are not configured, use an annotated tag (`git tag -a`) rather than a lightweight
tag. After the first GHCR publication, confirm the package is public and inherits access from this
repository. Do not move or reuse a published version tag; publish a new version instead.
