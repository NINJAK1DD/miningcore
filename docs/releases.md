# Installing a prebuilt Miningcore release

GitHub Releases provide two tested, framework-dependent Linux x64 builds: a primary **Ubuntu 26.04**
archive and a separately compiled **Ubuntu 22.04 compatibility** archive. The release container is
built from the Ubuntu 26.04 archive. Each archive saves compilation time, but still requires the
.NET 10 ASP.NET Core runtime and Miningcore's native runtime libraries.

Use only the archive matching the host release. Native libraries built on Ubuntu 26.04 can require
a newer glibc and are not represented as compatible with Ubuntu 22.04 or 24.04. Ubuntu 24.04 remains
a tested source-build target through `build-ubuntu-24.04.sh`. Windows and other Linux distributions
are likewise not represented as binary-compatible by these archives; use the source-build guide in
the root README for those environments.

| Ubuntu host | Supported deployment path |
| --- | --- |
| 26.04 LTS x64 | Primary release archive, primary container, or source build |
| 24.04 LTS x64 | Tested source build; do not use either prebuilt archive |
| 22.04 LTS x64 | Compatibility release archive or source build |

> **Runtime requirement:** install a supported, serviced .NET 10 ASP.NET Core runtime from the
> documented Ubuntu package source and keep it updated with normal security maintenance.

TLS-enabled Stratum endpoints rely on the host security policy and accept TLS 1.2 or TLS 1.3 on
supported, patched hosts. Legacy miners limited to TLS 1.0 or TLS 1.1 must be upgraded or replaced.

If this replaces an existing .NET 6 deployment, first follow the dedicated
[.NET 6 to .NET 10 migration guide](dotnet-6-to-10-migration.md). Do not treat the clean-install
commands below as an instruction to overwrite a live configuration or database.

Use this guide by task:

| Task | Start here |
| --- | --- |
| New installation | [Choose a version](#choose-a-version) |
| Upgrade or rollback | [Upgrade or roll back](#upgrade-or-roll-back) |
| Container deployment | [GitHub Container Registry image](#use-the-github-container-registry-image) |
| Existing RC.9 operator | [RC.11 highlights](#rc11-highlights) |
| Runtime behavior changes | [Operational and compatibility changes](#operational-and-compatibility-changes) |
| Release maintainer | [Maintainer release procedure](#maintainer-release-procedure) |
| Interrupted publication | [Recover an interrupted publication](#recover-an-interrupted-publication) |

For a failed live deployment, begin with the [troubleshooting guide](troubleshooting.md) rather than
copying a recovery command from the maintainer section.

## RC.11 highlights

`v0.1.0-rc.11` advances the supported build and release pipeline without adding a database migration
or changing the live pool configuration contract from RC.9.

- Ubuntu 26.04 x64 is the primary archive, container and Linux development target.
- Ubuntu 22.04 x64 retains its separately compiled compatibility archive.
- Ubuntu 24.04 x64 remains a required source-build target.
- Native builds fail immediately when any hashing component fails and verify the complete shared
  library inventory before packaging.
- GitHub Release and GHCR publication is staged, digest-pinned, serialized across release tags and
  recoverable after an interrupted run without silently replacing conflicting evidence.
- Release publication treats GitHub Actions installation tokens as opaque values, including the
  stateless `ghs_APPID_JWT` format, while rejecting unsafe control characters at the HTTP boundary.
- The scheduled Ubuntu image-pin monitor uses a strict, bounded, line-oriented internal handoff and
  fails closed on malformed or ambiguous results.

The release-pipeline items are maintainer-facing. Operators should still verify the selected
archive, provenance and host compatibility, and should read the cumulative operational changes
before upgrading from an older release candidate.

## Choose a version

Versions containing a suffix such as `v0.1.0-rc.1` are release candidates. Test them before relying
on them for real funds. A version without a suffix, such as `v0.1.0`, is a stable release and updates
the `latest` container tag.

Open the [releases page](https://github.com/NINJAK1DD/miningcore/releases), choose a version, and
download the archive matching the host and the checksum manifest:

- `miningcore-VERSION-linux-x64-ubuntu-26.04.tar.gz` (choose this on Ubuntu 26.04)
- `miningcore-VERSION-linux-x64-ubuntu-22.04.tar.gz` (choose this on Ubuntu 22.04)
- `SHA256SUMS`

The examples below use `v0.1.0-rc.11`. Substitute the version you selected.

```console
export MININGCORE_VERSION=v0.1.0-rc.11
MININGCORE_UBUNTU=
MININGCORE_RELEASE_READY=
MININGCORE_INSTALL_READY=
MININGCORE_DOWNLOAD_DIR=
archive=
if [ -r /etc/os-release ]; then
  MININGCORE_HOST_RELEASE="$(
    (. /etc/os-release; printf '%s:%s' "$ID" "$VERSION_ID")
  )"
else
  MININGCORE_HOST_RELEASE=unknown
fi
if [ "$(uname -m)" != x86_64 ]; then
  echo "STOP: prebuilt release archives require x86_64" >&2
else
  case "$MININGCORE_HOST_RELEASE" in
    ubuntu:22.04|ubuntu:26.04)
      export MININGCORE_UBUNTU="${MININGCORE_HOST_RELEASE#ubuntu:}"
      ;;
    *)
      echo "STOP: use the documented source-build path on $MININGCORE_HOST_RELEASE" >&2
      ;;
  esac
fi
if [ -n "$MININGCORE_UBUNTU" ]; then
  archive_name="miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}.tar.gz"
  if MININGCORE_DOWNLOAD_DIR="$(
    mktemp -d "${TMPDIR:-/tmp}/miningcore-release.XXXXXXXX"
  )"; then
    archive="$MININGCORE_DOWNLOAD_DIR/$archive_name"
    archive_part="${archive}.part"
    checksum_file="$MININGCORE_DOWNLOAD_DIR/SHA256SUMS"
    checksum_part="${checksum_file}.part"
    release_url="https://github.com/NINJAK1DD/miningcore/releases/download/${MININGCORE_VERSION}"
    if curl --fail --location --output "$archive_part" "$release_url/$archive_name" &&
      curl --fail --location --output "$checksum_part" "$release_url/SHA256SUMS" &&
      mv -- "$archive_part" "$archive" &&
      mv -- "$checksum_part" "$checksum_file" &&
      (cd "$MININGCORE_DOWNLOAD_DIR" &&
        sha256sum --ignore-missing --check --strict SHA256SUMS); then
      export MININGCORE_RELEASE_READY=1
      echo "READY: $archive is verified and ready to install"
    else
      echo "STOP: release download or checksum verification failed" >&2
      rm -f -- "$archive" "$archive_part" "$checksum_file" "$checksum_part"
      rmdir -- "$MININGCORE_DOWNLOAD_DIR"
      MININGCORE_DOWNLOAD_DIR=
      MININGCORE_UBUNTU=
      archive=
    fi
  else
    echo "STOP: unable to create private release download directory" >&2
    MININGCORE_DOWNLOAD_DIR=
    MININGCORE_UBUNTU=
    archive=
  fi
fi
```

The host-release check prevents accidental cross-distribution installation. Downloads use unique,
private temporary storage and are renamed from `.part` files only after curl succeeds. This avoids
overwriting unrelated files in the operator's current directory and remains compatible with the
curl version supplied by Ubuntu 22.04. `SHA256SUMS` covers both archives; `--ignore-missing` limits
verification to the selected archive, while `--strict` rejects a malformed checksum line.
Do not continue to the runtime and installation steps unless the block selected an archive and its
checksum verification succeeded. The block deliberately returns to an interactive shell after a
`STOP` message instead of closing an SSH session.

GitHub also publishes build provenance for each archive. If the
[GitHub CLI](https://cli.github.com/) is installed, verify it with:

```console
if [ "${MININGCORE_RELEASE_READY:-}" = 1 ]; then
  gh attestation verify "$archive" --repo NINJAK1DD/miningcore
else
  echo "STOP: no release archive passed the download and checksum gate" >&2
fi
```

## Install runtime dependencies

On the primary Ubuntu 26.04 target, install Canonical's framework and native runtime packages:

```console
sudo apt-get update
sudo apt-get install -y \
  aspnetcore-runtime-10.0 \
  libboost-locale1.90.0 \
  libboost-regex1.90.0 \
  libboost-serialization1.90.0 \
  libgmp10 \
  libsodium23 \
  libzmq3-dev
```

`libsodium23` and the versioned Boost/GMP packages are runtime-only providers. `libzmq3-dev` is a
deliberate exception: the vendored `ZeroMQ.dll` imports `libzmq`, so Linux needs the unversioned
`libzmq.so` symlink supplied by that package; `libzmq5` alone supplies only `libzmq.so.5`. Every
release-affecting pull request builds both the source and packaged Dockerfiles, validates the current
apt package names and `ldd -r` provider closure, and performs a managed ZeroMQ load inside each final
image. Apt package names are not OCI image references and therefore remain outside the digest-based
release image-pin monitor.

On the Ubuntu 22.04 compatibility target, enable Canonical's supported .NET backports PPA first:

```console
sudo apt-get update
sudo apt-get install -y software-properties-common
sudo add-apt-repository -y ppa:dotnet/backports
sudo apt-get update
sudo apt-get install -y \
  aspnetcore-runtime-10.0 \
  libboost-locale1.74.0 \
  libboost-regex1.74.0 \
  libboost-serialization1.74.0 \
  libgmp10 \
  libsodium23 \
  libzmq3-dev
```

## Install the archive

Create a dedicated service account, unpack the versioned directory, and point a stable symlink at
it:

```console
MININGCORE_INSTALL_READY=
if [ "${MININGCORE_RELEASE_READY:-}" = 1 ]; then
  release_dir="/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}"
  if (
    set -e
    id -u miningcore >/dev/null 2>&1 || \
      sudo useradd --system --home-dir /var/lib/miningcore --shell /usr/sbin/nologin miningcore
    sudo mkdir -p /opt /etc/miningcore /var/lib/miningcore /var/log/miningcore
    sudo tar -xzf "$archive" -C /opt
    test -d "$release_dir"
    if [ ! -e /etc/miningcore/config.json ]; then
      sudo cp "$release_dir/config.example.json" /etc/miningcore/config.json
    fi
    sudo chown -R miningcore:miningcore /var/lib/miningcore /var/log/miningcore
    sudo chown root:miningcore /etc/miningcore
    sudo chown root:miningcore /etc/miningcore/config.json
    sudo chmod 0750 /etc/miningcore
    sudo chmod 0640 /etc/miningcore/config.json
    sudo ln -sfnT "$release_dir" /opt/miningcore
  ); then
    MININGCORE_RELEASE_READY=
    if rm -f -- "$archive" "$checksum_file" &&
        rmdir -- "$MININGCORE_DOWNLOAD_DIR"; then
      MININGCORE_DOWNLOAD_DIR=
      archive=
      checksum_file=
    else
      echo "WARN: remove the verified release files from $MININGCORE_DOWNLOAD_DIR" >&2
    fi
    export MININGCORE_INSTALL_READY=1
    echo "READY: installed $release_dir and updated /opt/miningcore"
  else
    echo "STOP: installation failed; /opt/miningcore was not changed" >&2
  fi
else
  echo "STOP: no release archive passed the download and checksum gate" >&2
fi
```

The stable symlink is changed only after extraction and filesystem setup succeed. On upgrades, the
existing `/etc/miningcore/config.json` is retained; compare it with the new example and apply changes
deliberately. A successful installation also removes the verified archive and checksum workspace;
an explicit warning names the directory if cleanup cannot complete.

The archive's `examples/` directory contains CI-validated direct, multi-coin, merged-mining and
distributed-recorder starting points. They do not replace the live configuration automatically;
choose a topology from `examples/README.md`, stage it separately, and apply local secrets and
network settings deliberately.

Compare the extracted metadata with the binary before changing the live service:

```console
if [ "${MININGCORE_INSTALL_READY:-}" = 1 ] &&
    [ -n "${release_dir:-}" ] && [ -d "$release_dir" ]; then
  cat "$release_dir/BUILD-INFO"
  LD_LIBRARY_PATH="$release_dir" "$release_dir/Miningcore" --version
else
  echo "STOP: no release from this installation run is available to verify" >&2
fi
```

`BUILD-INFO` must name the selected release, matching Ubuntu target, and source commit. Releases
published after the version-reporting validation was introduced must report the same semantic
version (without the tag's leading `v`) and full commit SHA. Older releases, including
`v0.1.0-rc.2`, can show the legacy `0.1.0.0-BRANCH` format; match their full embedded SHA to
`BUILD-INFO` instead. A branch label such as `dev` is not sufficient release provenance by itself.

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
export MININGCORE_VERSION=v0.1.0-rc.11  # Replace with the release you selected.
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

### Ubuntu 26.04 primary release and source-build support

The repository now includes `build-ubuntu-26.04.sh` and a dedicated Ubuntu 26.04 source-build CI
lane. Ubuntu 26.04 is now also the primary prebuilt archive, container base, and Linux development
target. The native sources build with GCC 15 and Boost 1.90: the CryptoNight interfaces use explicit
byte-output conversions and POSIX declarations, the CryptoNote library uses C++14 with its direct
Boost MPL dependency, and obsolete Boost.System linkage has been removed from CryptoNote and
ZanoNote. ZanoNote also uses the current Boost.Asio `io_context` and Boost.UUID initialization
forms.

The Linux native build driver now propagates each component failure explicitly and stops before a
later component can hide an incomplete build. The Ubuntu 26.04 validation publishes the shared
24-library inventory also required by release packaging, checks x86-64 architecture, managed
exports and dynamic relocation providers, and runs targeted CryptoNote, Flex, yescrypt and ZanoNote
load tests against the freshly built libraries. It also exercises version/help/schema paths and
reaches a controlled startup safety boundary. Ubuntu 24.04 retains required source-build
validation, and an official compatibility archive remains independently built and fully tested on
Ubuntu 22.04 x64. Do not deploy the 26.04 archive on an older host; select the matching archive or
build from source.

Supported source-build helpers force stable English diagnostics and fail if the compiler or build
system emits a warning. The normal pull-request build and source-container build enforce the same
contract, while managed warnings are also promoted structurally to errors. The warning cleanup
repairs the reported managed-code and native-library findings instead of hiding them globally,
including undefined behavior in CryptoNight, Argon2, Ethash, Xelis, Verus and libkeccak code. The
CryptoNight soft-shell buffer defect was in a currently unregistered algorithm path, but is fixed
to keep that native implementation memory-safe if it is enabled later.

Three Linux hashing defects are corrected and deserve particular attention from operators:

- Argon2d previously left `blake2b_long` unresolved in `libmultihash.so`. Calling `argon2d250`,
  `argon2d500`, `argon2d1000` or `argon2d16000` could therefore terminate Miningcore on the first
  hash. The implementation is now linked into the library, uses a collision-resistant internal
  symbol name, clears its working state and is pinned by a known-answer test independently checked
  against a reference Argon2 implementation.
- Allium was registered and exported, but its implementation object was absent from
  `libmultihash.so`. An Allium share could therefore terminate Miningcore on the first hash. The
  implementation is now linked and pinned by a vector independently cross-checked against the
  Garlicoin project's published `allium-hash` package.
- Xelis v1 on a CPU without AES-NI previously called unavailable OpenSSL symbols and implemented a
  full AES block encryption where the mining algorithm requires one AES round. The portable path
  now uses the same single-round operation as Xelis v2. A no-AES build and known-answer test run on
  every supported Linux lane; AES-capable lanes additionally compare it directly with AES-NI.

All 24 packaged Linux hashing libraries now link with `-Wl,--no-undefined`. CryptoNote explicitly
links its Boost.Regex, OpenSSL and libsodium providers and includes its parsing-only Miningcore
stubs; Dero includes HighwayHash's runtime instruction-set resolver; and ZanoNote includes the
cryptographic and proof objects its parsing exports reference. This closes latent load-time failures
that were previously outside the strict `libmultihash.so` boundary. CryptoNote's Bulletproof and
Bulletproof+ sizing helpers now reject malformed proof shapes without throwing, every exported C ABI
entry point catches native exceptions, and isolated hostile miner-transaction vectors prevent a
daemon-supplied block template from unwinding C++ through P/Invoke. The managed fast-hash declaration
also returns `void`, matching `cn_fast_hash_export` exactly instead of ignoring a synthetic integer.

Release and source-build validation derives every `DllImport` and `LibraryImport` from all managed
native-wrapper sources, tolerating formatting changes while failing if any attribute cannot be
parsed unambiguously. Each wrapper must map to exactly one library in
`scripts/release/linux-native-libraries.txt`, every listed library must have exactly one wrapper,
and every managed entry point must be a callable function in that library's dynamic export table.
Nonliteral entry-point expressions and conditional imports inside the reviewed `Native` directory
fail structurally instead of being guessed. A separate lightweight scan rejects direct literal
imports of packaged libraries elsewhere in source-controlled application code without applying the
wrapper grammar to unrelated operating-system P/Invokes. The shared attribute grammar recognizes
qualified and aliased `DllImport`/`LibraryImport` names, attribute targets and lists, and both
extensionless and exact `.so` library names regardless of line layout. Generated `bin` and `obj`
trees are excluded so a future `LibraryImport` source generator cannot create a false duplicate
contract.

Provider-aware `ldd -r` and weak-import inspection then reject missing dependencies and unresolved
native-to-native relocations for every artifact. ELF version suffixes are normalized before matching
the narrow standard-toolchain weak-symbol allowlist and the exception manifest, whose entries must
use canonical unversioned symbol names. Missing, symlinked or otherwise non-regular inputs, malformed
tool output and failed inspection tools also fail closed. Contract mismatches use status 1; defects
in the validator input or inspection process use status 70. The validator supports a precise,
per-library JSON exception manifest for a future genuinely optional provider, but the current release
has no exceptions; any configured exception must be observed or validation rejects it as stale.

When adding a Linux native library, add its sorted filename to the inventory, give it one managed
wrapper with literal library and entry-point names, export every imported symbol, and link the
shared object with `-Wl,--no-undefined`. Add all direct provider libraries and implementation
objects to its Makefile rather than relying on another plugin to have been loaded first. The
previous sibling-plugin assumption is deliberately reversed: each packaged shared object must now
prove its provider closure independently, regardless of library load order. The
hermetic symbol-contract suite automatically discovers new wrappers and tests missing exports,
unresolved providers, malformed inputs, exact exception scoping and stale exceptions. The Ubuntu
22.04 and 26.04 release artifacts and Ubuntu 24.04 and 26.04 source builds run the same real-artifact
contract before packaging or smoke testing.

The four Ethash-family libraries run synthetic light-cache vectors that exercise the corrected
temporary-node lifetime without allocating a production-size DAG. Those vectors pin stability;
separate development-versus-corrected-build comparison found the digests identical, confirming
that the lifetime repair is output-neutral. The Ubuntu native-vector lanes also run RandomX and
RandomARQ known-answer tests against the exact patched release artifacts. The pinned RandomX-family
sources are verified by SHA-256 before patches are applied. Raising their CMake policy floor to 3.10
selects CMake's newer policy defaults through that version; the native vectors protect the hashing
contract.

The Equihash memory-cleanse helper now compiles correctly on Windows and uses guaranteed volatile
byte stores on non-Windows targets. This removes its undeclared OpenSSL dependency without relying
on taking the address of a C++ standard-library function.

For diagnosis on a future, unsupported compiler only, an operator may set
`MININGCORE_ALLOW_BUILD_WARNINGS=1` when invoking a user-facing source-build helper. The warnings
remain visible and the helper labels the result unsuitable for release. This override cannot bypass
an unreadable audit log and applies only to the post-build native/compiler/build-system diagnostic
audit. Managed compiler warnings and NuGet security advisories remain errors and cannot be bypassed
with this variable. The override is never enabled by CI or release packaging; resolve every warning
before deploying the artifact.

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

Administrative routes remain subject to the public API rate limiter before the admin IP whitelist
and bearer token are evaluated. This preserves abuse control and bounds rejection-log amplification
from unauthorized sources. Loopback is exempt by default; trusted remote automation that requires
an exemption must add its narrowly scoped source address to `api.rateLimiting.ipWhitelist` as well
as `api.adminIpWhitelist`. The rate-limit IP whitelist bypasses API throttling globally, not only
for administrative requests, so restrict its entries to trusted fixed addresses.

### Security hardening: bounded protected-route rejection logging

Source-IP whitelist rejection logs for `/api/admin` and `/metrics` are now bounded against log
amplification. Each route family owns an independent, fixed-size limiter: its first rejection is
written at `Info`, intervening rejections are counted, and the next informational entry after the
one-minute monotonic interval includes the suppressed count. Per-request details remain available
at `Debug`; enabling that level during hostile traffic can substantially increase log volume.

A summary remains pending until another rejection arrives after the interval, and the source shown
on it belongs to that current request rather than the potentially different suppressed sources.
Varying attacker addresses does not increase limiter state, and a metrics flood cannot suppress
the first administrative rejection. Bearer-authentication rejection logging retains its separate
per-pipeline limiter.

New Prometheus counter `miningcore_api_ip_whitelist_rejections_total`
increments for every whitelist rejection regardless of log suppression. Its `route_family` label
is restricted to the fixed values `admin`, `metrics` or `other` and never contains a source address
or request path, preserving bounded metric cardinality under hostile traffic.

Authorization behavior is unchanged. Every client outside the applicable whitelist still receives
`403 Forbidden`, including when `api.rateLimiting.disabled` is `true`. Exact supported `/metrics`
scrapes continue to bypass the public API rate limiter, but rejected scrapes can no longer flood
normal informational logs. This is log-volume containment rather than request throttling; retain
dedicated listeners, narrow source whitelists, TLS where required and host/network firewall rules.

### Security hardening: protected browser resources and metrics methods

API-pipeline responses for the administrative and Prometheus route families now send
`Cross-Origin-Resource-Policy: same-origin`, `Cache-Control: no-store` and
`X-Content-Type-Options: nosniff` on success and on wrong-listener, rate-limit, whitelist,
authentication, credential-unavailable and method rejections. Protocol errors rejected by Kestrel
before a request enters the pipeline cannot carry these application headers. The resource policy
blocks eligible cross-origin no-CORS subresource use, but does not generally prohibit navigation or
iframe embedding. These headers do not prevent requests from being sent and are not a replacement
for listener isolation, IP whitelists, admin bearer authentication, TLS or firewall policy.

After existing listener, rate-limit and IP-whitelist controls accept a request, the `/metrics` route
family now accepts only the exact, case-sensitive `GET` and `HEAD` method tokens. `HEAD` returns the
normal exposition headers without a body, but still performs the full registry collection and
serialization server-side. It is not a cheaper high-frequency liveness probe. `OPTIONS`, `POST`,
lowercase lookalikes such as `get`, and every other method return an empty `405 Method Not Allowed`
response with `Allow: GET, HEAD` without invoking the exporter. Rejected listener and client
identities keep their existing `404`, `429` or
`403` response. Exact scrapes bypass the public API rate limiter, while rejected lowercase,
mixed-case and unsupported method tokens remain throttled. Ordinary Prometheus scrapes and
command-line `GET` clients require no change.
Custom health checks that incorrectly use another method must switch to `GET` or `HEAD`.

### Security hardening: metrics CORS isolation

The Prometheus `/metrics` route family no longer receives the public API's permissive CORS headers.
This applies case-insensitively on both dedicated metrics listeners and legacy shared listeners;
segment-bounded matching leaves public lookalikes such as `/metrics-export` unchanged. Public REST
and WebSocket routes retain their existing CORS behavior, while `/api/admin` remains restricted.

Prometheus, `curl` and other non-browser scrapers require no changes because CORS is a browser
control. A custom browser dashboard that directly scraped `/metrics` from another origin must move
that access behind a deliberately secured same-origin telemetry service. Listener isolation,
`metricsIpWhitelist`, TLS and firewall behavior are unchanged.

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
application whitelist alone does not block a route forwarded by that proxy.

Explicit API ports must
be unique and in the range 1–65535. Enabled internal Stratum ports must also be in that range;
port `0` is now rejected instead of creating an unpredictable ephemeral mining endpoint.
TLS-enabled deployments use the same configured certificate on every listener. An API listener
that uses the same port and an overlapping bind address as an enabled local Stratum endpoint now
stops startup with the conflicting port identified; different specific bind addresses may reuse a
port.

Enabled Stratum endpoints follow the same address-aware rule: two pools may share a numeric
port on distinct specific IPv4 or IPv6 addresses, while identical addresses, wildcards and
IPv4-mapped equivalents fail startup with both pool and endpoint identities. All overlapping pairs
are reported together so operators can correct the complete configuration before restart. See
[API listener isolation](configuration.md#api-listener-isolation).

Every enabled internal Stratum port must map to an endpoint object. A JSON `null` endpoint now
stops normal startup with the affected pool and numeric port identified instead of being treated as
an omitted loopback address. Disabled and relay-only pools retain deferred listener validation, and
`-rs` recovery continues to discard listener settings because it opens no Stratum sockets.

An enabled relay-only pool remains available through the public API, but unusable null endpoint
entries are omitted from its public `ports` map instead of causing the complete pool response to
fail. Miningcore warns at startup when an enabled relay-only pool retains such an entry. API reads
now project listener settings into dedicated public endpoint DTOs rather than mapping or mutating
the live configuration type. The public DTOs have no TLS credential fields or trusted
PROXY-protocol peer allow-list, preventing those runtime-only values from entering the response
even when legacy null serialization is enabled.

Consequently, `ports[*].tlsPfxFile` and `ports[*].tlsPfxPassword` change from `null` to absent.
`ports[*].tcpProxyProtocol.proxyAddresses` was previously returned with the configured trusted-proxy
allow-list and is now absent entirely. This is an intentional information-disclosure hardening change.
REST clients must remove references to those private fields; the remaining endpoint keys retain their
existing names and values.

Consumers compiling directly against Miningcore response classes must also update the generic value
type of `PoolInfo.Ports` from `PoolEndpoint` to `ApiPoolEndpoint`.

Normal startup now requires a non-null `paymentProcessing` object in every configured pool entry,
including disabled pools. Keep the object and set its `enabled` value to `false` when payouts must
remain disabled. An omitted object or explicit JSON `null` now stops startup with the affected pool
identified, rather than allowing a later API or payout path to fail with a null reference. The
`-rs` share-recovery command continues to discard this live-service setting and can import durable
shares from a damaged configuration. As a defensive response boundary, an invalid programmatic
pool with no payment configuration omits `paymentProcessing` from `/api/pools` and
`/api/pools/{id}` instead of fabricating disabled or zero-valued payout defaults.

`PoolInfo.ShareBasedBanning` now uses the dedicated
`ApiPoolShareBasedBanningConfig` response type instead of exposing
`PoolShareBasedBanningConfig` directly. This is a source- and binary-level .NET API type change for
consumers that reference Miningcore assemblies; those consumers must update the property type and
rebuild. The JSON contract on `/api/pools` and `/api/pools/{id}` is unchanged: `enabled`,
`checkThreshold`, `invalidPercent`, `time`,
`minerEffortPercent` and `minerEffortTime` retain their names, values and existing null behavior.
The separation is preventative API-contract hardening rather than remediation of a known leak.

Public pool-response redaction now removes every case-insensitive duplicate of known blockchain
wallet-password and wallet-private-key settings from the untyped `paymentProcessing` extension-data
bag. Earlier builds could return one live credential through `/api/pools` and `/api/pools/{id}` when
the configuration contained the same sensitive setting more than once with case-variant names, such
as both `WalletPassword` and `walletPassword`. The affected redaction paths are Alephium, Bitcoin,
Ergo, Handshake and Kaspa wallet passwords plus Warthog wallet private keys.

Operators can assess exposure by inspecting their configuration locally for duplicate sensitive
names after ignoring case.
If checking `/api/pools`, use a trusted local connection, avoid saving or sharing the response and
treat any key under `paymentProcessing.extra` matching `walletPassword` or `walletPrivateKey`
without regard to letter case as exposed. Operators who used such a configuration should upgrade,
remove the duplicate entries and rotate every wallet password or private key that may have appeared
in a public response. Treat reverse-proxy, client and monitoring logs containing those responses as
sensitive until their retention period has expired or they have been securely removed.

`paymentProcessing.extra` is now a typed, family-aware public projection instead of an untyped
copy-and-redact dictionary. The REST response remains nested and preserves the configured spelling,
JSON scalar type, value and explicit-null presence of approved fields, including coercible legacy
representations. Unknown settings, malformed or non-scalar values, case-ambiguous duplicates and
wallet credentials are omitted. This also closes a pre-existing
disclosure path for Bitcoin wallet-password settings reused by Equihash, Nexa, ProgPoW and
SatoshiCash, whose family names were outside the former redaction switch. Operators using those
families should inspect trusted local copies of prior `/api/pools` or `/api/pools/{id}` responses
and related proxy, client or monitoring logs for any key matching `walletPassword` without regard
to case; if found, rotate that wallet password and handle retained responses as sensitive data.

The REST names and normal/legacy null behavior of approved non-sensitive fields are preserved.
JSON object member order is not a public contract and may differ from configuration order. Unknown
extension fields are intentionally no longer returned. Direct .NET consumers must change
`ApiPoolPaymentProcessingConfig.Extra` from `IDictionary<string, object>` to
`ApiPoolPaymentProcessingExtra` and rebuild. External Newtonsoft re-serialization no longer
flattens entries; it emits the typed `Extra` property as a nested object using the consumer's
contract resolver (`extra` with a camel-case resolver). See
[the public API contract](api.md#pool-response-contracts) for the complete allow-list.

REST consumers must continue accepting runtime-coercible legacy scalar representations rather than
inferring the JSON type from the new .NET property type.

Normal startup now reports unrecognised or malformed `paymentProcessing.extra` entries for enabled
pools that are omitted from the typed public response. The one-time, family-aware diagnostics use
the same public projection and runtime-binder contract, so recognised runtime-only wallet
credentials remain silent rather than being mislabelled as unknown. Actionable warnings distinguish
unknown keys, ambiguous case variants, unsupported public scalars and conversion failures. They
never log values, replace every sensitive-looking omitted key name with
`<redacted-sensitive-key>`, escape unsafe characters in ordinary names within a fixed output-length
bound and emit at most ten key warnings per pool plus one reason-grouped remainder summary. A
redacted unknown-key warning can list the family's recognised private field names as safe spelling
hints without echoing the supplied name.

Disabled pools, share recovery and API requests do not emit these warnings. They use the dedicated
`PaymentExtraDiagnostics` NLog category for independent
routing or filtering through the standard console and main log. Per-pool files remain limited to
their pool-id logger. A private entry can still be active runtime configuration when the coin
family's binder accepts it; operators should correct or remove a warning-producing setting only
after checking that family-specific contract.

Cluster and coin-template configuration loading no longer allows Json.NET to interpret full
ISO-looking date-time strings as dates. UTC, offset and unsuffixed date-time values now survive
normal startup, recovery-mode configuration loading, typed payment-extension projection,
parsed-configuration output and approved REST responses. Date-only strings were already preserved
and remain covered by regression tests. This intentionally corrects earlier builds that could
replace configured text with a normalized, culture-dependent date representation.

It affects
extension values such as Handshake `walletName` or `walletAccount`, Kaspa
`versionEnablingMaxFee`, custom
coin-template extension values, and any other configured string that resembles a full date-time.
System.Text.Json and Newtonsoft deserialization of the public payment DTO from JSON text now
preserve the same typed string value. Newtonsoft clients that first materialize a `JObject` must use
`DateParseHandling.None`; the DTO rejects an already-coerced `JTokenType.Date` because its exact
lexical value can no longer be recovered.

Operators or API clients that relied on the normalized
value must instead configure the literal they require and update that dependency before upgrading.
RPC, Stratum, recovery-journal and schema-file readers retain their existing parsing behavior.

Enabled internal Stratum sockets are now pre-bound and retained as one all-or-nothing cluster
startup phase. A non-local address, occupied endpoint, invalid IPv6 scope or other bind failure stops
startup before any pool is announced online and releases all sockets already acquired by that
attempt. The failure identifies the pool, effective endpoint and operating-system socket error.
Broadcast and multicast listener addresses are rejected during configuration validation, while
IPv4 loopback and link-local ranges remain eligible for the authoritative host bind. Existing valid
listener configurations require no migration.

Reserved sockets remain bound but do not call
`Listen` until their pool finishes initialization; activation must succeed before the pool announces
`Online`. Reserved listeners are exclusive rather than `SO_REUSEADDR`-enabled, and all server-
initiated accepted-socket closes—including ordinary host shutdown, malformed requests, TLS handshake
failures, request-handler faults and independent send-timeout cancellation—use abortive cleanup.
Accepted sockets are protected against
unclean process termination by default, while only genuine peer-initiated EOF switches to graceful
close. This permits bytes already written to the network to drain but does not drain Miningcore's
application send queue during shutdown.

Startup retries `AddressAlreadyInUse` with one cluster-wide
bounded retry-delay budget totalling up to 90 seconds when residual `TIME_WAIT` survives an unclean
stop; scheduled waits do not multiply with the number of endpoints. Bind-call duration and scheduler
overshoot remain outside that delay budget, so it is not a hard wall-clock deadline.
Active-interface masks are used
only to reject known subnet-directed IPv4 broadcast identities; ordinary addresses still use bind as
the host-specific source of truth.

This lifecycle intentionally changes the source-level extension surface: the protected
`StratumServer.RunAsync(CancellationToken, StratumEndpoint[])` and `PoolBase.RunStratum` helpers are
no longer available to out-of-tree subclasses. Custom pool implementations must use the base
`PoolBase.RunAsync` lifecycle so they cannot bypass cluster-scoped reservation, activation-before-
online ordering or retained-socket cleanup. Miningcore does not provide a compatibility helper that
would silently restore the unsafe per-pool bind path. The protected surface now also provides
`CreateConnectionId` and `BeforeConnectionTaskRemovalAsync` lifecycle hooks, while
`UnregisterConnection` fails fast when the identity is absent instead of relying on a Debug-only
assertion. Out-of-tree subclasses must not call it defensively for an already-removed connection.

After a terminal completion or error callback has been invoked, an exception from that callback or
subsequent stream teardown is logged and absorbed: `DispatchAsync` completes without issuing a
second terminal callback. Operators diagnosing lifecycle-callback programming errors must therefore
inspect the connection error logs rather than expecting a faulted dispatch task.
The native resolver contains an explicit FreeBSD libc fallback, but FreeBSD is not runtime-tested in
CI and is not promoted to a first-class supported Miningcore deployment target by this change.

Listener-only validation is skipped during `-rs` share recovery because that mode opens no API or
Stratum sockets. Recovery stream-rebuilds the top-level configuration from `logging`, `persistence`,
`pools`, `shareRecoveryFile`, `shareRecoveryStateDirectory` and optional `coinTemplates`. Malformed
or duplicate live-only API, statistics, relay, banning, notification, NiceHash, memory,
mining-concurrency and cluster-identity settings therefore cannot block a durable-share import or
recovery-state command. Strict duplicate, schema and CLR validation remains for the settings the
recovery command consumes. After ambiguity checks, each pool is similarly rebuilt from its required
ID plus optional string coin metadata, so stale listener, wallet, daemon, payout, banning, recipient,
timing and extension fields are excluded from the recovery boundary.

Recovery logging is now narrowed to the console level and colour settings consumed by the one-shot
command. Stale file-only logging values cannot block import, and an absent, null or wholly malformed
logging section receives a default informational, non-coloured console logger so recovery progress
remains visible. Invalid but correctly typed log-level names now fail during configuration
validation with the accepted NLog names listed, rather than surfacing later during logger setup.
Committed-journal retirement now remains resumable if a historical pool ID is removed from the
configuration after the database commit. The resume path still proves the PostgreSQL manifest,
record count and content hash before destructive retirement and never replays the committed data.
That committed cleanup also no longer depends on the current AuxPoW indexes; fresh or unproven
imports still require them before their transaction begins.

Recovery also sanitizes optional `coinTemplates` metadata before schema validation. Valid custom
template paths are retained, while non-string entries or a malformed non-array value cannot block
share import, verification or acknowledgement. Normal startup continues to reject those values.

Share import now validates the configuration boundary it actually consumes. `-rs` accepts an
all-disabled pool set and returns from preflight before mining, hashing or native solver
initialization. Pool IDs and complete PostgreSQL persistence remain mandatory. Recovery partition
preflight checks every configured pool ID, and those IDs form an explicit journal allowlist: an
unknown or mistyped record ID fails before a pending marker, transaction or manifest registration.
Validated journal records that use merged-mining block persistence trigger the AuxPoW
idempotency-index preflight before the import transaction begins.
Best-effort template loading now includes disabled pools so recovered block notifications retain coin
metadata when available; missing paths, missing metadata or undefined coins warn and continue.
Normal startup remains unchanged and strict.

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

On supported Ubuntu Linux hosts, no-replacement publication uses
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

Only positive-percentage `rewardRecipients` are omitted from public payment history. A configured
zero-percent recipient is inactive: if that address also mines, its ordinary payout remains visible
in the `payments` table and API. This makes the zero-percent examples behaviorally inert while
preserving the established privacy treatment for active pool-fee and donation recipients.

Before enabling Bitcoin-family payments, confirm whether the pool or miners pay transaction fees.
Pool-paid fees require a confirmed spendable reserve because a matured coinbase may cover the
recipient outputs but not the additional fee, causing `sendmany` to return `Insufficient funds`
code `-6`. Normal `sendmany` requests can deduct fees from recipients when `minersPayTxFees` is
enabled, but per-recipient fallback submissions can still require additional spendable input. Follow the
[wallet-readiness and backup runbook](operations.md#bitcoin-family-payout-wallets); do not repair
balances or issue a replacement payment manually. A complete Dogecoin mainnet cycle on RC.8,
including this conclusive failure and scheduled recovery after funding, is recorded in the
[mainnet validation record](mainnet-validation.md#rc8-dogecoin-merged-mining-payout).

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

This section is for repository maintainers. Operators installing or recovering a service should use
the task links at the top of this guide and the [troubleshooting guide](troubleshooting.md).

### Build and package contract

The release workflow accepts SemVer tags reachable from `dev`, for example `v0.1.0-rc.11` or
`v0.1.0`. It first builds and smoke-tests the Ubuntu 26.04-based source `Dockerfile`, then builds and
fully tests separate Ubuntu 26.04 primary and Ubuntu 22.04 compatibility archives. The Jammy archive
is built inside an Ubuntu 22.04 job container on a maintained hosted runner, so its publication does
not depend on GitHub retaining the retiring `ubuntu-22.04` runner image. Both release lanes use a
stable hosted runner and an immutable, digest-pinned Docker Official Image.

The workflow-declared
build-image reference is recorded in each archive's `BUILD-INFO` and checked against the shared
release-target contract during collection; the in-container `VERSION_ID` check independently
confirms the selected Ubuntu release. Each lane runs the complete PostgreSQL-backed and ZeroMQ test
suite, validates native runtime links, and checks that the binary reports the release version and
source commit.

The workflow then verifies the two-archive set, creates one checksum manifest, smoke-tests the
26.04 packaged image, and publishes both archives and the container with provenance. Publication
uses an explicit, recoverable sequence: an unpublished draft receives and verifies the archive set,
a version-scoped staging tag records the container digest, the draft records that digest in
`CONTAINER-IMAGE.json`, and only a verified, published GitHub Release permits the public version
tags and mutable aliases to move.

The published container intentionally follows the serviced .NET Resolute runtime tag so rebuilt
images receive upstream security fixes. BuildKit attaches maximum provenance and an SBOM to record
the resolved build materials without freezing that runtime tag indefinitely. A weekly workflow
compares the archive-build tags with their reviewed manifest-list digests and fails visibly when a
pin needs review; updating a pin still requires the complete release validation. That scheduled
monitor runs independently of lint tooling, while the always-running .NET pull-request workflow
enforces ShellCheck for the release scripts.

### Image-pin monitor contract

Pin drift exits with status 1. A registry failure uses advisory status 69 only when its diagnostic
matches a known transient network, service or rate-limit condition. Missing tags and every
unclassified inspection failure use status 70 and fail closed. The checker inspects every target
before deciding its final status, so a transient failure on one target cannot hide confirmed drift
on another. An advisory workflow warning names only the image tags for which no drift decision could
be made. Authentication failures remain fatal unless the registry diagnostic independently
identifies rate limiting. Missing Docker, Buildx or `imagetools inspect`, and unparseable resolver
output, also use status 70 because the monitor itself needs repair.

In GitHub Actions, untrusted registry and proxy diagnostics are printed only while
workflow-command processing is suspended, preventing their contents from creating annotations or
changing runner state. Each safe header, its evidence and the subsequent advisory warning share
stdout with confirmed-drift and monitor-validation diagnostics, making multi-target execution order
deterministic. This ordering guarantee covers messages emitted by the scripts; Bash runtime warnings
bypass their output helpers and may still appear on stderr. If a random guard token cannot be
created, the diagnostic remains one prefixed, shell-escaped physical line. Both Actions command
sentinels are rewritten. Source evidence is capped at 4,096 characters. Its encoded representation
is capped at 8,192 characters, with explicit truncation markers. Embedded CR/LF and command-shaped
data are therefore encoded rather than emitted as runner input. Malformed resolver evidence uses the
same guarded output path.

The wrapper supplies `MININGCORE_IMAGE_PIN_RESULT_FILE` as a private machine-readable handoff so
checker diagnostics remain live. The checker uses the central contract and writes exactly one
unresolved canonical image tag per line in central release-target order.

The wrapper limits each read to the configured target count plus one line. It then
accepts only a non-empty, unique, in-order subset of the configured tags before constructing a
warning from matched contract values. Invalid-line diagnostics identify only the safe,
locally derived line number; they never repeat handoff content. Result-file failures also use a
generic diagnostic rather than exposing the private path.

Empty or overlong files, blank lines,
whitespace or carriage-return variants, unknown, duplicate or out-of-order tags, and result-file
creation, read or write failures use structural status 70 and never produce a workflow warning. A
final byte comparison against the canonical serialization also rejects binary contamination and a
missing terminal newline. A standalone checker with no private result file retains its single
readable, comma-separated summary on stderr. Confirmed pin drift remains status 1; only recognized
transient resolution failures use status 69.

> **Branch-protection note:** `Verify reviewed Ubuntu image pins` is deliberately path-filtered and
> does not report a status on unrelated pull requests. Do not configure it as a required status
> check; require the always-running build and release checks instead.

The tagged build injects the validated tag and commit as assembly metadata because development
branches intentionally retain GitVersion's prerelease calculation; the runtime check requires an
exact match before packaging can begin.
This additional source-container gate makes release runs longer but catches Dockerfile-only build
failures before publication. Prefer a signed annotated tag:

> **Release retry rule:** if any Release workflow job fails, select **Re-run all jobs**.
> Do not use **Re-run failed jobs**. GitHub Actions artifacts are scoped to a run attempt, so the
> collector may be rerun without a successful sibling archive from the earlier attempt and will
> correctly reject the incomplete set.

```console
git switch dev
git pull --ff-only origin dev
NEXT_VERSION=v0.1.0-rc.11  # Replace with the next unused SemVer version.
git tag -s "$NEXT_VERSION" -m "Miningcore $NEXT_VERSION"
git push origin "$NEXT_VERSION"
```

If signed tags are not configured, use an annotated tag (`git tag -a`) rather than a lightweight
tag. After the first GHCR publication, confirm the package is public and inherits access from this
repository. Do not move or reuse a published version tag; publish a new version instead.

### Recover an interrupted publication

GitHub Releases and GHCR are separate services and do not provide a shared transaction. The workflow
therefore treats publication as four observable states:

1. **No publication:** no release or version-scoped staging tag exists. Create an unpublished draft
   and upload the tested archives.
2. **Staged container:** the draft has `publication-staging-vX.Y.Z` and, once recorded,
   `CONTAINER-IMAGE.json`. Reuse that digest; never rebuild over it.
3. **Durable release:** the published archives and container record are cryptographically verified.
   Create or verify the immutable full-version tags.
4. **Promoted version:** `vX.Y.Z` and `X.Y.Z` match the recorded digest. When eligible, the newest
   stable release also owns `X.Y`, GHCR `latest` and GitHub's latest-release pointer.

The staging tag is deliberately retained as audit and retry evidence. Before the release becomes
durable, the workflow does not create either full-version container tag and does not move `X.Y` or
`latest`. Every draft is created with GitHub latest disabled. The repository-wide publication job
queues up to 100 release tags, processes one at a time and never cancels an active publication. This
keeps release freshness inspection and mutation inside one serialized boundary. A 60-minute job
timeout bounds head-of-line blocking while preserving all durable state for a full rerun.

Before uploading anything to an existing draft, the workflow requires its exact generated title and
a deterministic collision marker containing the repository, release tag and source commit. This
detects a stale or unrelated same-tag draft that this workflow did not create; it is not an
authorization control because a maintainer with release-write access can reproduce the marker. A
mismatch stops for human review before trusted assets are uploaded. This check applies only while
the release is a draft.

GitHub's release list may briefly lag a successful draft creation, asset upload or publication.
The workflow retries those visibility checks for a bounded period, always pins the retained numeric
release ID during asset work, and fails closed when authoritative state does not converge. Archive
uploads are streamed, use bounded connection, retry and total-time budgets, do not follow redirects,
and preserve GitHub's bounded error response in the failed-job log.

Actions installation tokens are opaque and may use GitHub's stateless `ghs_APPID_JWT` format. The
publisher rejects control characters and escapes curl's private configuration syntax, but does not
validate a token alphabet or parse its contents. Do not add token-format regexes or print a token
while troubleshooting authentication.

When a stable draft is published, the workflow compares it with every other published stable
release and explicitly chooses whether GitHub may mark it latest. After publication, each GHCR
destination is created from the recorded digest and inspected again. An older rerun leaves `X.Y`
unchanged when a higher patch exists in that line and leaves both GHCR and GitHub `latest` unchanged
when any higher stable version exists. A pre-existing immutable version tag with another digest, a
release asset with different bytes, missing recorded state, duplicate or unexpected assets, and any
non-authoritative GitHub or registry response stop with `HUMAN ACTION REQUIRED`; the workflow does
not overwrite the conflict. Registry absence is accepted only from an explicit manifest/name-unknown
or reference-bound not-found response. Authentication and permission errors deliberately fail closed.

For an interrupted tag, inspect both services without changing them. Replace the example values but
do not move the Git tag:

```console
export REPOSITORY=NINJAK1DD/miningcore
export TAG=v0.1.0-rc.11
export IMAGE=ghcr.io/ninjak1dd/miningcore
export STAGING_TAG="publication-staging-$TAG"

release_pages=$(mktemp)
release_json=$(mktemp)
trap 'rm -f -- "$release_pages" "$release_json"' EXIT

gh api --paginate --slurp \
  "repos/$REPOSITORY/releases?per_page=100" > "$release_pages"
jq -e --arg tag "$TAG" \
  '[.[][] | select(.tag_name == $tag)] |
   if length == 1 then .[0]
   else error("expected exactly one matching draft or published release") end' \
  "$release_pages" > "$release_json"
jq '{id,tag_name,draft,prerelease,assets:[.assets[].name]}' "$release_json"
docker buildx imagetools inspect "$IMAGE:$STAGING_TAG"
docker buildx imagetools inspect "$IMAGE:$TAG"
docker buildx imagetools inspect "$IMAGE:${TAG#v}"
```

For a stable release, also inspect `${TAG#v}` with its final `.patch` component removed and inspect
`$IMAGE:latest`. Continue in the same shell. To inspect the recorded digest without relying on
`gh release download` draft handling, download the asset through its authenticated API identifier:

```console
container_record=$(mktemp)
trap 'rm -f -- "$release_pages" "$release_json" "$container_record"' EXIT

asset_id=$(jq -er \
  '[.assets[] | select(.name == "CONTAINER-IMAGE.json")] |
   if length == 1 then .[0].id else error("container record is absent or ambiguous") end' \
  "$release_json")
gh api -H 'Accept: application/octet-stream' \
  "repos/$REPOSITORY/releases/assets/$asset_id" > "$container_record"
jq . "$container_record"
docker buildx imagetools inspect \
  "$(jq -r '.image' "$container_record")@$(jq -r '.digest' "$container_record")"
```

The tag endpoint is not a draft inspection command: GitHub documents it as returning a published
release. The authenticated, paginated list above includes drafts for callers with push access and
the exact-one check prevents an ambiguous tag from being selected. Publication requires GitHub CLI
2.51 or newer because draft discovery uses `gh api --slurp`. Once the list establishes a numeric
release ID, repeated checks within that command use the authenticated ID endpoint instead of
sweeping every release again. Release title and notes may be edited after publication; recovery
identity then comes from the tag, release ID, state and immutable asset/container evidence.

Do not manually edit the generated title or remove the collision marker while publication remains a
draft. If the workflow stops on either check, first compare the draft ID, author and audit history
with the failed workflow run and confirm that the Git tag still resolves to the recorded source
commit. When that evidence proves the draft belongs to this workflow and only its presentation was
edited, restore the exact generated title (for example, `Miningcore vX.Y.Z`) and original marker
before using **Re-run all jobs**. If ownership cannot be established, preserve the draft, assets and
run logs for review; only after confirming that it is unrelated should a maintainer delete the
draft. If a staging tag remains, complete the orphan-tag evidence and cleanup procedure below before
rerunning all jobs to create fresh workflow-owned state. Never make an unrelated draft pass by
copying the public marker.

If a retention policy prunes the staging tag after publication has completed, a rerun remains safe
when the release record and at least one immutable GHCR version tag still matches the recorded
digest. Any other immutable tag that is present must also match; conflicts fail closed. The
matching tag proves the digest remains live, and promotion safely recreates a missing sibling from
that digest. A missing staging tag while the release is still a draft remains a hard stop because no
durable promoted tag can prove the recorded content.

An orphaned staging tag with no matching draft or published release is also a hard stop. Preserve
its digest, registry metadata and failed-run logs first. After establishing that no release record or
public version tag ever referenced it, a maintainer may delete only that orphaned staging tag and use
**Re-run all jobs** to restart publication. Never delete a staging tag merely to bypass a digest or
asset conflict.

If the evidence is internally consistent, open the failed Release workflow run and select
**Re-run all jobs**. Do not use **Re-run failed jobs**, because the tested archives belong to one run
attempt. The rerun verifies existing assets, reuses the exact staged digest, and continues from the
first incomplete state. GitHub's server-computed SHA-256 and size avoid repeated
archive downloads when available; older records fall back to download-and-compare verification. If
a command above fails for a reason other than an
authoritative not-found response, or any digest/asset differs, stop: preserve the tag, draft, assets,
container tags and failed-run logs for review. Never delete, move, rebuild over, or manually replace
publication evidence to force the services to agree.
