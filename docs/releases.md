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
allows 60 seconds for the application's bounded clean shutdown:

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
daemon credentials.

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
packages the result, smoke-tests the packaged image, and publishes both artifacts with provenance.
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
