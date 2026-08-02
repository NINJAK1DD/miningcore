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

## Logging and disk recovery

Miningcore now rotates every configured NLog file natively before a write would grow it beyond
512 MiB and retains four archives per file target. Remove legacy Miningcore `logrotate` rules that
use `copytruncate`; combining both mechanisms can create sparse files, while restarting the service
from `postrotate` disconnects miners. See
[Log files and rotation](configuration.md#log-files-and-rotation) for capacity planning.

The database guide now includes a guarded [disk-exhaustion recovery runbook](database.md#recover-after-disk-exhaustion).
It restores storage, PostgreSQL and coin daemons in dependency order before Miningcore and links to
the existing payout-ownership reconciliation procedure for an unclean database-session loss.

Recovery-journal appends now roll back a partial write to the previous file length, force-flush the
rollback, and refuse to extend a pre-existing incomplete line or framed batch. First creation uses a
force-flushed temporary file, atomic rename and Linux parent-directory synchronisation. A first-byte
format magic and chained v2 batch trailers record contiguous sequence, previous-frame identity,
expected count, record SHA-256 and deterministic frame digest. Every chain is streamed and verified
at startup, on first fallback entry and before recovery import. Later appends verify cached file
identity/length and only hash the new frame, avoiding quadratic outage I/O. Each forced append also
commits an independent terminal sequence/digest anchor, detecting removal of a complete final frame.
A bounded persistence queue transfers overflow to one bounded emergency journal writer outside the
mining admission lock instead of accepting unlimited memory or blocked-caller backlogs. Graceful stop
drains acknowledged shares independently of hosted-service cancellation, limits its PostgreSQL
drain to 20 seconds, reserves 15 seconds for bounded transaction recovery/fatal handling, and uses
the remaining host/service-manager window to journal the complete
unresolved registry. The supplied systemd stop timeout is 90 seconds. If PostgreSQL
and the recovery journal both fail, Miningcore synchronously closes a coordinated share-acceptance
boundary: validated shares enter accounting before positive responses, concurrent healthy
admissions use shared access, fail-stop is exclusive, response queueing is synchronous, and queued
responses are cancelled. Exact fatal evidence is captured only after that exclusive gate drains all
earlier publication/response admissions, so its sidecar is the quiescent unresolved registry rather
than a pre-transition approximation. It then writes a persistent hashed fatal
latch in an independent service-owned state directory,
awaits a bounded critical administrative notification attempt, and exits with dedicated status 74
instead of continuing without durable share accounting. Candidate persistence uses the same direct
alert and mandatory latch path. The supplied systemd unit does not restart status 74, and every
normal startup—including relay nodes—remains blocked until reconciliation and explicit latch
removal. A later dual-target candidate loss upgrades an already-started general shutdown to status
74, writes a distinct immutable incident record and sends an escalation alert with the exact latch
path. The fixed latch remains small and is force-flushed in a hash-pending state before exact shares
are serialized once into a streamed, incrementally hashed sidecar, so serialization and incomplete
sidecar failures still block restart. The read-only `--verify-share-recovery-state` command
enumerates incident metadata, verifies sidecar hashes and allocation-bounded records through one
restrictively shared, identity-checked handle, and decodes/counts exact records without modifying
evidence. Memory exhaustion fails the command without attempting to continue. Journal readers cap
individual recovery lines at 1,048,576 characters. Frame-content hashes normalise
line endings to `\n`; the independent anchor closes the terminal-frame deletion gap for newly
committed frames, while incident checksums remain necessary for legacy history. State-directory I/O uncertainty also fails
closed with status 74. Terminal and import-marker absence is accepted only after exact directory
enumeration; directories, symbolic links, unsupported entries, malformed content and inaccessible
state fail closed during startup and the first fallback append. Configure
`shareRecoveryFile` as an
absolute path on separately monitored or reserved storage where possible; the recovery runbook
explains evidence preservation and atomic, manifested import verification.
Recovery import now uses a durable multi-phase source-retirement marker. Startup and fallback
appends remain blocked until the retained source's complete chain, anchor, semantic hash, record
count and file identity are revalidated, the committed source rename and parent-directory sync
finish, and the marker records archive durability, anchor-retirement authorisation and anchor
retirement while retaining the validated terminal sequence and digest. Interruption after anchor
removal therefore resumes without manual safety-state edits. The same non-writable file object is checked again after
rename. Rerunning recovery resumes that sequence without changing the manifest identity or replaying
records; filesystem aliases of the configured source are rejected. Operators must not import
overlapping reviewed files because manifests identify whole sources rather than individual shares.

Unexpected mapper, connection, transaction or repository failures now quiesce mining, force-flush
the complete unresolved registry to the recovery journal and stop with a general failure. If the
journal also fails, status 74 and the fatal latch remain authoritative. The PostgreSQL transaction
lifecycle is cancellation-aware and bounded through open, begin, repository commands, commit,
rollback and cleanup. Transaction then connection disposal run as one ordered background sequence
under a four-second aggregate wait bound, because ADO.NET disposal APIs do not accept cancellation.
If transaction disposal consumes that bound, connection disposal cannot overlap it and begins only
if the transaction call later returns. Cleanup that finishes after the aggregate deadline logs its
eventual success, cancellation, task fault or returned provider exception with the transaction
outcome, resource stage and elapsed time. Deadline classification is unconditional even when cleanup
finishes between timeout and exception handling: once commit outcome is known, cleanup can add
evidence but cannot replace it. Cleanup failure after a known commit removes that exact batch from replayable
state; cleanup failure while commit is uncertain remains secondary evidence and cannot replace the
uncertain outcome. A commit whose outcome cannot be proven is never copied to the importable journal;
exact share JSON is streamed to the sidecar referenced by the status-74 latch for reconciliation.
Active Stratum dispatch tasks and in-flight request handlers receive a five-second bounded drain
before Share Recorder intake closes; expiry closes admission and returns a non-zero stop without
consuming the recorder's reserved window. Fatal, terminal and
import state subdirectories are durably parent-synchronised on first creation. State and alias
inspection uses atomic no-follow regular-file handles on Linux and Windows. Fatal incidents now form
a sequence/previous-digest chain anchored by the fixed latch's tip and expected count; the first v3
incident also anchors all retained legacy-v2 incidents. After database reconciliation, the new
`--acknowledge-share-recovery-state` command re-verifies the complete evidence set, publishes an
immutable durable `.acknowledged` anchor, and removes only the active latch. Manual latch deletion
does not unblock startup, acknowledgement is resumable after interruption, later incidents extend
the acknowledged tip, and removing or changing covered evidence fails closed. Fatal, incident and
acknowledgement metadata verification uses strict UTF-8, an exact 64-KiB raw-byte total limit,
bounded lines and stable handle/path identity checks, matching the sidecar verifier's fail-closed
replacement detection. Imported-source retirement now also re-confirms the exact PostgreSQL
manifest through a fresh connection before any destructive rename or anchor removal.

These local-recorder guarantees do not turn `shareRelay` into an acknowledged transport. A relay
sender's positive response proves only local in-memory relay-queue admission, not remote receipt or
PostgreSQL persistence. Also, up to the normal 65,536-share local recorder queue remains volatile
during abrupt process or machine loss; the bound limits exposure but does not provide power-loss
durability.

## Payout and WebSocket compatibility

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

## Coin definition accuracy

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

## Choose a version

Versions containing a suffix such as `v0.1.0-rc.1` are release candidates. Test them before relying
on them for real funds. A version without a suffix, such as `v0.1.0`, is a stable release and updates
the `latest` container tag.

Open the [releases page](https://github.com/NINJAK1DD/miningcore/releases), choose a version, and
download these two files:

- `miningcore-VERSION-linux-x64-ubuntu-22.04.tar.gz`
- `SHA256SUMS`

The examples below use `v0.1.0-rc.1`. Substitute the version you selected.

```console
export MININGCORE_VERSION=v0.1.0-rc.1
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
sudo mkdir -p /etc/miningcore /var/lib/miningcore
sudo curl -fL \
  "https://raw.githubusercontent.com/NINJAK1DD/miningcore/${MININGCORE_VERSION}/config.example.json" \
  -o /etc/miningcore/config.json
sudo chown root:10001 /etc/miningcore/config.json
sudo chmod 0640 /etc/miningcore/config.json
sudo chown 10001:10001 /var/lib/miningcore
sudo docker pull ghcr.io/ninjak1dd/miningcore:v0.1.0-rc.1
sudo docker run -d \
  --name miningcore \
  --restart unless-stopped \
  -p 4000:4000 \
  -p 3032:3032 \
  -v /etc/miningcore/config.json:/etc/miningcore/config.json:ro \
  -v /var/lib/miningcore:/var/lib/miningcore \
  ghcr.io/ninjak1dd/miningcore:v0.1.0-rc.1
```

Publish every API and Stratum port used by your configuration. The container runs as fixed non-root
UID/GID `10001`; its configuration and state mounts must be readable/writable by that identity. Its
`127.0.0.1` is the container itself, so database and daemon endpoints must be reachable
from the container network.

## Maintainer release procedure

The release workflow accepts SemVer tags reachable from `dev`, for example `v0.1.0-rc.1` or
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
git tag -s v0.1.0-rc.1 -m "Miningcore v0.1.0-rc.1"
git push origin v0.1.0-rc.1
```

If signed tags are not configured, use an annotated tag (`git tag -a`) rather than a lightweight
tag. After the first GHCR publication, confirm the package is public and inherits access from this
repository. Do not move or reuse a published version tag; publish a new version instead.
