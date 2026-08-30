# BTCPool.co.uk Miningcore

[![.NET](https://github.com/NINJAK1DD/miningcore/actions/workflows/dotnet.yml/badge.svg?branch=dev)](https://github.com/NINJAK1DD/miningcore/actions/workflows/dotnet.yml)
[![Release](https://img.shields.io/github/v/release/NINJAK1DD/miningcore?include_prereleases)](https://github.com/NINJAK1DD/miningcore/releases)
[![License](https://img.shields.io/github/license/NINJAK1DD/miningcore)](LICENSE)

<img src="logo.png" width="150" alt="Miningcore logo">

This is the Miningcore distribution maintained for [BTCPool.co.uk](https://btcpool.co.uk)
SOLO mining service. The source repository is hosted by `NINJAK1DD`, and the `dev` branch is its
primary integration branch. It builds on the
[upstream Miningcore project](https://github.com/blackmennewstyle/miningcore) and retains credit to
the original authors and contributors.

> **Production status:** the software targets the supported .NET 10 LTS runtime, but production
> deployment still requires the operational controls below. Read
> [Production operation](#production-operation) and the
> live [mainnet validation record](docs/mainnet-validation.md) before using real
> funds.

## Features

- High-performance asynchronous Stratum servers.
- Multiple pools and currencies in one cluster; see the bundled [coin definitions](src/Miningcore/coins.json)
  and the [Scrypt definition provenance](docs/scrypt-coin-definitions.md).
- Native proof-of-work validation with fixed difficulty and variable difficulty (vardiff).
- SOLO, PPLNS and PROP payout schemes, plus transactional Bitcoin-family PPS accounting.
- PostgreSQL-backed shares, blocks, balances, statistics and payment processing.
- Fail-closed share accounting with bounded queues, an emergency recovery journal and queue metrics.
- Protected payout ownership and reconciliation for interrupted or uncertain wallet submissions.
- Cluster-wide, exclusive Stratum listener reservation with address-aware safe port reuse.
- Share relays for advanced distributed pool deployments.
- REST API and WebSocket notifications, with bearer-authenticated, route-isolated administration
  and dedicated Prometheus listeners.
- Typed public API projections that keep wallet credentials and listener secrets out of responses.
- Integrated banning, TLS options, native log rotation and administrative notifications.
- Litecoin parent-chain and Dogecoin AuxPoW merged mining with independently selected SOLO, PPS,
  PROP or PPLNS accounting per pool.
- Versioned Ubuntu release archives, non-root containers and source-build paths.

## Quick start

This path installs the signed prebuilt release on a new **Ubuntu 26.04 x64** host, creates
PostgreSQL, prepares the configuration, and runs Miningcore under systemd. Run each block only after
the preceding check succeeds. Ubuntu 22.04 uses its separately built compatibility archive and
different runtime packages; Ubuntu 24.04 is a source-build target. Use the
[release guide](docs/releases.md) for those paths or for an upgrade/rollback.

### 1. Confirm the host and install dependencies

```console
. /etc/os-release
printf 'OS=%s %s\nARCH=%s\n' "$ID" "$VERSION_ID" "$(uname -m)"
```

Continue with this quick path only when it prints `OS=ubuntu 26.04` and `ARCH=x86_64`. Then install
the framework, native runtime providers, PostgreSQL and download tools:

```console
sudo apt-get update
sudo apt-get install -y \
  aspnetcore-runtime-10.0 \
  ca-certificates \
  curl \
  libboost-locale1.90.0 \
  libboost-regex1.90.0 \
  libboost-serialization1.90.0 \
  libgmp10 \
  libsodium23 \
  libzmq3-dev \
  openssl \
  postgresql
sudo systemctl enable --now postgresql
sudo systemctl is-active postgresql
```

### 2. Download and verify Miningcore

Select the release and download it into private temporary storage:

```console
export MININGCORE_VERSION=v0.2.0
export MININGCORE_UBUNTU=26.04
download_dir="$(mktemp -d "${TMPDIR:-/tmp}/miningcore-release.XXXXXXXX")"
archive_name="miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}.tar.gz"
release_url="https://github.com/NINJAK1DD/miningcore/releases/download/${MININGCORE_VERSION}"
curl --fail --location --output "$download_dir/$archive_name" \
  "$release_url/$archive_name"
curl --fail --location --output "$download_dir/SHA256SUMS" \
  "$release_url/SHA256SUMS"
(cd "$download_dir" && \
  sha256sum --ignore-missing --check --strict SHA256SUMS)
```

Do not continue unless the checksum command reports the selected archive as `OK`. If the GitHub CLI
is installed, also verify the release provenance:

```console
gh attestation verify "$download_dir/$archive_name" --repo NINJAK1DD/miningcore
```

### 3. Install the versioned application

```console
id -u miningcore >/dev/null 2>&1 || \
  sudo useradd --system --home-dir /var/lib/miningcore --shell /usr/sbin/nologin miningcore
release_dir="/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}"
sudo mkdir -p /opt
sudo tar -xzf "$download_dir/$archive_name" -C /opt
test -d "$release_dir"
sudo install -d -m 0750 -o root -g miningcore /etc/miningcore
sudo install -d -m 0750 -o miningcore -g miningcore \
  /var/lib/miningcore /var/log/miningcore
sudo install -m 0640 -o root -g miningcore \
  "$release_dir/config.example.json" /etc/miningcore/config.json
sudo ln -sfnT "$release_dir" /opt/miningcore
cat "$release_dir/BUILD-INFO"
LD_LIBRARY_PATH="$release_dir" "$release_dir/Miningcore" --version
```

The version output must match the selected tag and include its source commit. Keep
`/opt/miningcore` as the stable symlink; future upgrades install another immutable versioned
directory before changing that link.

### 4. Create PostgreSQL and load the schema

Create a dedicated role without putting its password on the command line:

```console
sudo -u postgres createuser --pwprompt miningcore
sudo -u postgres createdb --owner=miningcore miningcore
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f /opt/miningcore/migrations/createdb.sql
psql -h 127.0.0.1 -U miningcore -d miningcore \
  -c 'SELECT current_database(), current_user;'
```

The final command prompts for the new password and must report database/user `miningcore`. Existing
database operators should stop here and follow the
[upgrade and migration runbook](docs/database.md#upgrade-an-existing-database) instead of running
the new-database schema over live data.

### 5. Choose and edit a configuration

The installed `config.example.json` is the fully annotated reference. Smaller reviewed pool,
multi-coin, merged-mining and relay files are under `/opt/miningcore/examples/`; copy one over the
starter only when it matches the intended topology. Then edit the protected file:

```console
sudoedit /etc/miningcore/config.json
```

Before continuing:

- replace every `CHANGE_ME` wallet, RPC, PostgreSQL, SMTP, TLS and licence value;
- use the PostgreSQL password created above and keep daemon/wallet RPC listeners private;
- remove unused pools or leave them explicitly disabled;
- preserve a non-null `paymentProcessing` object on every pool;
- use unique pool IDs and Stratum ports, and create one payout wallet per enabled coin;
- set `logging.logBaseDirectory` to `/var/log/miningcore`;
- set `shareRecoveryFile` to `/var/lib/miningcore/recovered-shares.txt` and
  `shareRecoveryStateDirectory` to `/var/lib/miningcore`; and
- keep direct examples `SOLO` unless the [PPS operator checklist](docs/pps.md) is complete.

This check must print nothing. Any output names a placeholder that still needs attention:

```console
sudo grep -n 'CHANGE_ME' /etc/miningcore/config.json || true
```

### 6. Install, secure and synchronize the coin daemons

Miningcore does not install or manage the full nodes and payout wallets named by `pools[].daemons`.
Install each daemon from its authoritative project, verify its release, bind RPC to a private
interface, use a unique strong RPC credential, and allow the node to synchronize fully. Create the
pool payout wallet, encrypt and back it up, and test the documented restore procedure away from
production. Do not expose daemon RPC or wallet RPC to the internet.

Coin-specific RPC, wallet and extension requirements vary. Start with the chosen file in the
[example index](examples/README.md), then check the matching definition in
[`coins.json`](src/Miningcore/coins.json) and the daemon's pinned/released documentation. Miningcore
must not be started until every enabled pool's daemon and wallet endpoint is reachable using the
credentials in `/etc/miningcore/config.json`.

### 7. Optional: partition the `shares` table

Skip this for a first or small pool. List partitioning is an advanced multipool optimization. On a
new, still-empty database, it may be enabled before the first Miningcore start; on any database that
already contains shares, use the complete backup/restore procedure in
[Advanced share-table partitioning](docs/database.md#advanced-share-table-partitioning).

The appendix deletes and rebuilds `shares`, so first preserve even the empty baseline:

```console
umask 077
sudo -u postgres pg_dump -Fc -d miningcore > "$HOME/miningcore-before-partition.dump"
pg_restore --list "$HOME/miningcore-before-partition.dump" > /dev/null
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f /opt/miningcore/migrations/createdb_postgresql_11_appendix.sql
```

Create one partition for every pool ID that the configuration can record. Replace the example table
name and value; the value must exactly match `pools[].id`:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore
```

```sql
SET ROLE miningcore;
CREATE TABLE public.shares_bitcoin_solo
PARTITION OF public.shares
FOR VALUES IN ('bitcoin-solo');
RESET ROLE;
\q
```

Repeat the `CREATE TABLE` statement for each pool, including an auxiliary pool whose direct Stratum
listener is disabled. Miningcore fails startup when a required partition is missing.

### 8. Install and start the systemd service

Generate the administrative API token outside `config.json`, protect it, then install the supplied
unit:

```console
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env >/dev/null
unset token
sudo chown root:root /etc/miningcore/miningcore.env
sudo chmod 0600 /etc/miningcore/miningcore.env
sudo cp /opt/miningcore/systemd/miningcore.service \
  /etc/systemd/system/miningcore.service
sudo systemctl daemon-reload
sudo systemctl enable --now miningcore
sudo systemctl status miningcore --no-pager -l
```

The unit runs as the unprivileged `miningcore` account, creates persistent state/log directories,
allows Miningcore's bounded clean shutdown to finish, and prevents an unsafe automatic restart
after dual persistence failure (exit status 74).

Before starting a remotely hosted pool, configure the provider firewall and host firewall without
locking out the administration path. Permit only the intended Stratum/TLS and public API ports.
Keep PostgreSQL, daemon/wallet RPC, the administrative API and metrics private or explicitly
allow-listed; place public HTTP traffic behind the documented TLS reverse proxy.

### 9. Verify before admitting miners

```console
sudo journalctl -u miningcore --since '10 minutes ago' --no-pager
sudo ss -ltnp
curl --fail --max-time 5 http://127.0.0.1:4000/api/health-check
curl --fail --max-time 5 http://127.0.0.1:4000/api/pools
curl --fail --max-time 5 http://127.0.0.1:4002/metrics --output /dev/null
sudo -u postgres psql -d miningcore -c \
  'SELECT poolid, count(*) FROM shares GROUP BY poolid ORDER BY poolid;'
```

Use the configured ports if they differ. Read the full startup log and require every intended daemon
to be synchronized, every wallet to be backed up and usable, and every intended pool to be online.
Connect one representative miner, confirm accepted shares reach PostgreSQL, then test a clean stop
and start before opening the service to production traffic. Continue with
[Production operation](#production-operation) for firewall, TLS, reverse-proxy, backup, monitoring
and recovery requirements.

### Documentation

| I want to… | Read… |
| --- | --- |
| Install, upgrade or roll back a release | [Release guide](docs/releases.md) |
| Choose a ready-to-edit pool or relay topology | [Example configuration index](examples/README.md) |
| Configure pools, logging and recovery storage | [Configuration guide](docs/configuration.md) |
| Operate and monitor a production service | [Operator handbook](docs/operations.md) |
| Diagnose a startup, mining, payout or storage problem | [Troubleshooting guide](docs/troubleshooting.md) |
| Set up, back up or recover PostgreSQL | [Database and recovery guide](docs/database.md) |
| Use advanced share-table partitioning | [Partitioning runbook](docs/database.md#advanced-share-table-partitioning) |
| Back up and restore payout wallets | [Wallet backup runbook](docs/operations.md#wallet-backups) |
| Use the API, WebSocket events or metrics | [API guide](docs/api.md) |
| Secure and call administrative routes | [Administrative API security](docs/admin-api-security.md) |
| Deploy distributed Stratum/recorder roles | [Share-relay guide](docs/share-relays.md) |
| Enable direct Bitcoin-family PPS | [PPS operator guide](docs/pps.md) |
| Validate a new deployment before miners | [Operator preflight](docs/operations.md#before-accepting-miners) |
| Migrate an existing .NET 6 deployment | [.NET 6 to .NET 10 migration guide](docs/dotnet-6-to-10-migration.md) |
| Enable Litecoin–Dogecoin merged mining | [Merged-mining guide](docs/merged-mining-litecoin-dogecoin.md) |
| Review newly added Scrypt daemon contracts | [Scrypt coin definitions](docs/scrypt-coin-definitions.md) |
| Review Bitcoin-family BIP310 mask safety | [Version rolling](docs/version-rolling.md) |

The complete [documentation index](docs/README.md) also links dependency, licensing and validation
references.

## Bitcoin-family PPS

PPS credits each valid share when its PostgreSQL accounting transaction commits. A later confirmed,
stale or orphaned block does not add or reverse that liability, so the operator—not the miner—owns
block variance and must maintain a monitored liquidity reserve. Support is currently restricted to
audited Bitcoin-family pools, including either chain in the integrated Litecoin/Dogecoin topology.

Existing databases need the candidate-idempotency, payout-ownership and share-accounting migrations
before PPS is enabled. The reviewed direct examples remain `SOLO` by default so copying one cannot
silently opt the operator into a financial liability. Follow the [PPS operator guide](docs/pps.md)
for the configuration change, commissioning checks, exact ledger, monitoring and recovery boundary.

## Litecoin–Dogecoin merged mining

Merged mining exposes the Litecoin Stratum endpoint to miners and submits qualifying copies of the
same Scrypt proof to Dogecoin. Both pools must be enabled and have their own payout wallet address.
Each independently selects `SOLO`, `PPS`, `PROP` or `PPLNS`; mixed combinations are supported.
Non-SOLO Dogecoin accounting requires `requireAuxAddress: true`. PPS transfers block variance and
liquidity risk to the operator, so read the reserve and migration guidance before enabling it. PPS
statistical shares default to seven-day retention and exactly-once accounting receipts to a 30-day
replay horizon; size or archive them using the [database guide](docs/database.md#share-accounting-retention-and-sizing).

The Litecoin pool points to the Dogecoin pool with this block:

```json
"mergedMining": {
  "enabled": true,
  "auxPoolId": "doge-solo",
  "addressParameter": "doge",
  "requireAuxAddress": true,
  "auxiliaryTemplatePollTimeoutMs": 500
}
```

Miners connect to the **Litecoin** Stratum port. Put the Litecoin payout address in the username and
the Dogecoin payout address in the password:

```text
Username: YOUR_LTC_ADDRESS.rig01
Password: doge=YOUR_DOGE_ADDRESS
```

Example ccminer command without a requested starting difficulty:

```console
ccminer -a scrypt -o stratum+tcp://pool.example:3032 -u YOUR_LTC_ADDRESS.rig01 -p "doge=YOUR_DOGE_ADDRESS"
```

A concrete syntax example using valid-format documentation addresses is shown below. These addresses
have no usable private key and must never be used to receive mining rewards:

```console
ccminer -a scrypt -o stratum+tcp://pool.example:3032 -u Lbr1z8RSnJSTdxyrZUeSSLSJMVLbxT9KHZ.rig01 -p "doge=DMmAGB4G146gvAUJ7vehi5Y92Qhd7TSMS2"
```

To request difficulty `65536`, combine the ordinary `d=` password option with the DOGE address:

```console
ccminer -a scrypt -o stratum+tcp://pool.example:3032 -u YOUR_LTC_ADDRESS.rig01 -p "d=65536;doge=YOUR_DOGE_ADDRESS"
```

Do not put the DOGE address in the username or connect merged miners to the auxiliary DOGE port.
The pool validates both addresses before authorising the worker. See the commented
[configuration example](config.example.json) and read the
[complete merged-mining guide](docs/merged-mining-litecoin-dogecoin.md) before enabling it.

## Build and installation

### Prebuilt Ubuntu x64 releases

Download a release archive and `SHA256SUMS` from the
[releases page](https://github.com/NINJAK1DD/miningcore/releases), verify it, and follow the
[prebuilt installation and upgrade guide](docs/releases.md). The binary is framework-dependent and
therefore still needs the documented .NET 10 and native runtime dependencies. No Windows or generic
cross-distribution binary compatibility is claimed. Use the primary Ubuntu 26.04 archive on 26.04;
use the separately built compatibility archive on 22.04. Ubuntu 24.04 operators should build from
source rather than use either distribution-specific archive.

Already running Miningcore on .NET 6? Read the
[.NET 6 to .NET 10 operator migration guide](docs/dotnet-6-to-10-migration.md) before changing the
runtime, application files, service or database. It covers release archives, source deployments and
containers, including rollback planning and preserving an existing configuration.

### Debian and Ubuntu

Run the script matching the installed operating system from the repository root. The script installs
the native build dependencies and .NET SDK, then publishes Miningcore into `build/`.

| Operating system | Command | Guidance |
| --- | --- | --- |
| Debian 12 | `./build-debian-12.sh` | **Recommended script path** |
| Ubuntu 26.04 LTS x64 | `./build-ubuntu-26.04.sh` | **Primary release/source target** |
| Ubuntu 24.04 LTS x64 | `./build-ubuntu-24.04.sh` | Tested source-build compatibility target |
| Ubuntu 22.04 LTS x64 | `./build-ubuntu-22.04.sh` | Tested source and compatibility archive |

For example:

```console
chmod +x build-debian-12.sh
./build-debian-12.sh
ls build/Miningcore
```

These scripts install the .NET 10 SDK and publish the `net10.0` application. Ubuntu 24.04 and 26.04
use Canonical's native .NET 10 packages without Microsoft's APT feed or the Ubuntu 22.04
`dotnet/backports` PPA. GitHub Actions
([workflow source](.github/workflows/dotnet.yml)) is the authoritative automated build-and-test path.
Interactive builds retain .NET's concise progress and elapsed-time display; a separate private
MSBuild log is audited and removed when the helper exits, while warnings remain visible and fatal.
The standard `MSBUILDTERMINALLOGGER=off` environment setting remains available for accessibility,
terminal compatibility and log-processing requirements.

### Windows development

Windows is supported for development and testing, not recommended for hosting a production pool.

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Optionally install [Visual Studio 2026](https://visualstudio.microsoft.com/vs/) with the
   **.NET desktop build tools** and **Desktop development with C++** workloads.
3. Clone the repository and open [Miningcore.sln](src/Miningcore.sln), or run:

```dosbatch
build-windows.bat
```

The published files are written to `build`.

### Docker Engine

Install [Docker Engine for your Linux distribution](https://docs.docker.com/engine/install/) and
confirm it works:

```console
sudo docker run --rm hello-world
MININGCORE_VERSION=v0.2.0  # Replace with the release you selected.
sudo docker pull ghcr.io/ninjak1dd/miningcore:${MININGCORE_VERSION}
```

Pin a published version rather than copying the example version indefinitely. Copy and edit the
configuration, then run the image. This example uses a fixed Docker bridge gateway, publishes the
public API, binds the admin and metrics ports to host loopback, and publishes the merged-mining LTC
Stratum port. Publish every additional port used by your configuration:

```console
MININGCORE_VERSION=v0.2.0  # Replace with the release you selected.
sudo mkdir -p /etc/miningcore /var/lib/miningcore
sudo curl -fL \
  https://raw.githubusercontent.com/NINJAK1DD/miningcore/${MININGCORE_VERSION}/config.example.json \
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
sudo docker network create --driver bridge \
  --subnet 172.30.56.0/24 --gateway 172.30.56.1 miningcore
sudoedit /etc/miningcore/config.json
```

The administrative token must remain a 64-character hexadecimal value; the `openssl` command above
generates the required format.

In the configuration, replace the container-local loopback whitelist entries for the two published
protected ports with the fixed bridge gateway:

```json
"adminIpWhitelist": [ "172.30.56.1" ],
"metricsIpWhitelist": [ "172.30.56.1" ]
```

If that subnet overlaps the host network, choose another unused private subnet and use its selected
gateway in both whitelist entries.

Then start Miningcore:

```console
sudo docker run -d \
  --name miningcore \
  --restart unless-stopped \
  --env-file /etc/miningcore/miningcore.env \
  --network miningcore \
  -p 4000:4000 \
  -p 127.0.0.1:4001:4001 \
  -p 127.0.0.1:4002:4002 \
  -p 3032:3032 \
  -v /etc/miningcore/config.json:/etc/miningcore/config.json:ro \
  -v /var/lib/miningcore:/var/lib/miningcore \
  ghcr.io/ninjak1dd/miningcore:${MININGCORE_VERSION}
```

To build the same source locally instead:

```console
git clone https://github.com/NINJAK1DD/miningcore.git
cd miningcore
git checkout dev
sudo docker build -t btcpool-miningcore:local .
```

Run it with the same `/etc/miningcore/config.json` and `/var/lib/miningcore` mounts:

```console
sudo docker run -d \
  --name miningcore \
  --restart unless-stopped \
  --env-file /etc/miningcore/miningcore.env \
  --network miningcore \
  -p 4000:4000 \
  -p 127.0.0.1:4001:4001 \
  -p 127.0.0.1:4002:4002 \
  -p 3032:3032 \
  -v /etc/miningcore/config.json:/etc/miningcore/config.json:ro \
  -v /var/lib/miningcore:/var/lib/miningcore \
  btcpool-miningcore:local
```

Useful management commands:

```console
sudo docker logs -f miningcore
sudo sh -c '
  . /etc/miningcore/miningcore.env
  printf "Authorization: Bearer %s\n" "$MININGCORE_ADMIN_API_TOKEN" |
    curl --fail --header @- \
      http://127.0.0.1:4001/api/admin/stats/gc
'
curl --fail http://127.0.0.1:4002/metrics --output /dev/null
sudo docker stop miningcore
sudo docker rm miningcore
```

`docker restart` does not reload `--env-file`. After rotating the administrative token, changing a
version, or changing container creation options, remove and recreate the container with the full
version-pinned `docker run` command above. See the
[administrative API security guide](docs/admin-api-security.md#rotate-or-revoke) for safe token
rotation.

Remember these container boundaries:

- `127.0.0.1` inside a container means the container itself, not the Docker host.
- Miningcore must be able to reach PostgreSQL and every coin daemon through controlled network
  routes. Services on the Docker host normally use the selected bridge gateway rather than
  container-local loopback.
- Host traffic on a published port normally appears from the bridge gateway. If a protected request
  returns `403`, confirm the address in Miningcore's unauthorized-request log before changing a
  whitelist.
- A containerised Prometheus service should use a dedicated network and a predictable whitelisted
  address.
- Native hashing libraries can depend on CPU architecture and features, so build locally only on
  hardware compatible with the production host.

The full release, checksum, provenance, container and update procedure is in the
[release guide](docs/releases.md).

## Database setup

Miningcore uses PostgreSQL for shares, blocks, balances, statistics and payments. For a new public
pool, use a currently supported PostgreSQL release; PostgreSQL 15 or newer is a sensible baseline.

After [installing PostgreSQL](https://www.postgresql.org/download/), open its administrative shell:

```console
sudo -u postgres psql
```

Create a user and database. Choose a unique strong password and do not commit it to Git:

```sql
CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'CHANGE_ME_TO_A_STRONG_PASSWORD';
CREATE DATABASE miningcore OWNER miningcore;
\q
```

Import the current schema from the repository root:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/createdb.sql
```

Test the login:

```console
psql -h 127.0.0.1 -U miningcore -d miningcore -c "SELECT current_database();"
```

Then place the same connection details in `config.json`:

```json
"persistence": {
  "postgres": {
    "host": "127.0.0.1",
    "port": 5432,
    "user": "miningcore",
    "password": "CHANGE_ME_TO_A_STRONG_PASSWORD",
    "database": "miningcore"
  }
}
```

Back up before upgrades:

```console
sudo -u postgres pg_dump -Fc -d miningcore > miningcore-backup.dump
pg_restore --list miningcore-backup.dump > /dev/null
```

Using the local PostgreSQL administrator ensures the archive also contains partition tables or
other administrator-created objects that the runtime role may not be allowed to lock directly. For
a remote database, use a dedicated backup role with read and lock access to every schema object.

> [!IMPORTANT]
> The current release series is a breaking database upgrade for every deployment that enables payment
> processing, even when LTC/DOGE merged mining is not enabled. Stop Miningcore and apply
> `add_payout_manager_ownership.sql` before starting the upgraded binary. Recovery-only nodes that
> use `-rs` require the same migration. Missing ownership/idempotency schema fails startup with the
> migration filename instead of running payouts without protection.

Pooled merged-mining and direct Bitcoin-family PPS deployments also require
`add_share_accounting.sql`. Existing SOLO/SOLO merged mining retains its established share record
and does not require that additional migration.
It creates the paired-share identity and PPS liability ledger; do not enable either feature until
the migration and startup preflight succeed.

For an existing database, stop writers and payout managers before applying the migrations required by
the target release. The [database and upgrade guide](docs/database.md) gives the exact commands, restore
procedure, post-migration ownership check, merged-mining indexes, payout-manager ownership rules and
optional advanced partitioning.

> [!IMPORTANT]
> If you opt into the advanced partitioned `shares` layout, create a partition whose bound exactly
> matches every enabled pool ID before starting Miningcore. Startup now fails fast when a direct
> recorder, relay receiver or recovery import is missing one; see the database guide for the
> backup, conversion, partition creation and restore sequence.

## Configuration

Copy [config.example.json](config.example.json) to `config.json`. It is a JSON-with-comments example
covering the common cluster, API, PostgreSQL, statistics, banning, payment, daemon, low/high
Stratum, vardiff and LTC/DOGE merged-mining options. Miningcore accepts comments; ordinary
strict-JSON tools may not.

```console
cp config.example.json build/config.json
```

Replace all `CHANGE_ME` values and remove pools or services you do not intend to run. The
[example configuration index](examples/README.md) provides smaller direct, Bitcoin Cash,
multi-coin, merged-mining and distributed-recorder starting points. The
[configuration guide](docs/configuration.md)
explains the main sections and miner login formats. The machine-readable
[configuration schema](src/Miningcore/config.schema.json) validates the shared typed structure.
Coin-specific extension fields are intentionally outside that structural catalogue; use the
reviewed examples and the [coin-family guidance](docs/configuration.md#coin-specific-extension-fields)
for those settings.

## Running Miningcore

Start the published binary from the directory containing `config.json`:

```console
cd build
./Miningcore -c config.json
```

Miningcore validates the file and daemon connections during startup. Keep the console open for an
initial test; stop it with `Ctrl+C`. On Linux, confirm the API in another shell:

```console
curl http://127.0.0.1:4000/api/health-check
curl http://127.0.0.1:4000/api/pools
```

For unattended operation, use a service manager such as systemd, send logs to persistent storage and
configure clean shutdown timeouts. Do not run a production instance in `screen`, a desktop terminal,
or an interactive SSH session. See [Production operation](#production-operation).

### Basic PostgreSQL management

```console
# Open the database shell
psql -h 127.0.0.1 -U miningcore -d miningcore

# List tables inside psql
\dt

# Show recent blocks inside psql
SELECT poolid, blockheight, status, created FROM blocks ORDER BY created DESC LIMIT 10;

# Leave psql
\q
```

Use SQL only for inspection unless a documented migration or recovery procedure explicitly requires
a change. Manual edits to balances, blocks or payments can cause financial errors.

## API and web front ends

The API is enabled in the example on port `4000`. Dedicated `adminPort` and `metricsPort` listeners
keep protected route families off the public listener. Administrative requests additionally require
a bearer token kept outside the JSON configuration and public WebUI. See the
[administrative API security guide](docs/admin-api-security.md) and
[API listener configuration](docs/api.md#configuration) before publishing any HTTP port. Common
public endpoints include:

```text
GET /api/health-check
GET /api/pools
GET /api/pools/{poolId}
GET /api/pools/{poolId}/blocks
GET /api/pools/{poolId}/miners/{address}
GET /api/blocks
```

The fork has its own [API guide](docs/api.md), derived from the current controllers rather
than relying solely on an older upstream wiki. It includes v2 routes, WebSocket notifications,
metrics, rate limiting, admin-port isolation and reverse-proxy guidance.

Miningcore supplies an API, not a bundled public website. A community project such as
[btclinux/Miningcore.WebUI](https://github.com/btclinux/Miningcore.WebUI) can be used as a starting
point, but it targets another Miningcore fork and is not maintained, audited or endorsed by this
project. Review its current maintenance, licence and API assumptions, and deploy it behind your own
HTTPS reverse proxy before exposing it publicly. BTCPool.co.uk uses its own operational choices;
this reference is not a dependency.

## Deployment models

Most beginners should use a **direct node**: Miningcore, PostgreSQL access and payout processing on
one Linux host, with coin daemons on the same protected network. Distributed share relay deployments
are an advanced option.

| Role | What it needs |
| --- | --- |
| Direct pool/recorder | PostgreSQL and one payout manager |
| Non-merged database-free relay sender | Remote receiver/recorder; no local database |
| Merged-mining relay sender | PostgreSQL for synchronous block persistence |
| Central relay receiver/recorder | PostgreSQL; usually the sole payout/reconciliation owner |

Only one payout manager may own a pool/database set. Merged-mining nodes also have synchronous block
persistence and schema-preflight requirements. The full rules, crash recovery procedure and ZeroMQ
limitations are in the [merged-mining deployment guide](docs/merged-mining-litecoin-dogecoin.md).

## Caveats

- **Linux is the production target.** Windows builds are intended for development and testing.
- **Keep the host and .NET 10 serviced.** Apply supported security and runtime updates promptly.
- **Check each coin before enabling it.** Daemon, wallet, memory and native-file requirements vary by
  coin family; start with the bundled [coin definitions](src/Miningcore/coins.json) and the daemon's
  own documentation.
- **Keep private services private.** Never expose wallet RPC, daemon RPC, PostgreSQL, the admin API or
  internal relay ports to the public internet.
- **Prefer a direct deployment unless you need relays.** Ordinary ZeroMQ relay traffic is not a
  durable queue and is not replayed after an outage. Read the
  [share-relay guide](docs/share-relays.md) before distributing roles.
- **Plan for storage failure.** Put `shareRecoveryFile` on separately monitored or reserved storage
  where possible. If both PostgreSQL and the recovery journal fail, Miningcore deliberately stops
  accepting shares and requires the documented [recovery procedure](docs/database.md#recover-after-disk-exhaustion).
- **Allow clean shutdown to finish.** Miningcore reserves up to 45 seconds for accounting and recovery
  work; configure the service manager above that limit. The supplied systemd unit uses 90 seconds.

The detailed queue, journal, fail-stop and platform guarantees are documented under
[Share recovery storage](docs/configuration.md#share-recovery-storage). They are kept out of this
overview so operators can find the required actions without first reading implementation internals.

## Production operation

Before advertising a public pool:

- Run Miningcore on a maintained Linux release with a serviced .NET 10 runtime.
- Isolate daemon, wallet, PostgreSQL, admin API and relay ports with host/network firewalls.
- Put the public API and website behind an HTTPS reverse proxy; do not expose the admin API port.
- Enforce public-client request limits at that proxy. Miningcore does not recover the original
  client address from forwarded headers when the proxy connects over loopback.
- Keep hot-wallet balances limited. When the pool pays Bitcoin-family transaction fees, maintain a
  confirmed fee reserve; account for `minersPayTxFees` and per-recipient fallback behavior. Test
  daemon-generated wallet backups, database backups and configuration recovery.
- Use systemd or an equivalent supervisor with restart policy, resource limits and sufficient clean
  shutdown time. Keep its forced-stop timeout above Miningcore's 45-second internal budget.
- Monitor daemon sync, pool hashrate, rejected shares, uncertain blocks, reconciliation, disk space,
  PostgreSQL backups, wallet balances and payout ownership.
- Complete the [real-daemon validation plan](docs/merged-mining-regtest-validation.md) for merged
  mining. If the final relay hosts or route differ from the validated physical lab, repeat its
  firewall-interruption test on that exact production path.

Automated tests can be run with:

```console
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj
```

They cover consensus serialization, attribution and persistence regressions, but do not replace real
`litecoind`, `dogecoind`, wallet and PostgreSQL testing.

The [operator handbook](docs/operations.md) collects routine health, monitoring, stop/start and
backup checks. Start with the [troubleshooting guide](docs/troubleshooting.md) when a live service
reports an error or unexpected state.

## Contributions and support

Submit changes as pull requests targeting [the `dev` branch](https://github.com/NINJAK1DD/miningcore/tree/dev).
Use this repository's [issue tracker](https://github.com/NINJAK1DD/miningcore/issues) for reproducible
fork-specific bugs. Operational information for the hosted pool belongs on
[BTCPool.co.uk](https://btcpool.co.uk), not in GitHub issues.

The [upstream repository](https://github.com/blackmennewstyle/miningcore), its discussions and any
upstream commercial services are maintained separately and are not support offered by BTCPool.co.uk.

### Donations

Donations to support development and maintenance of this NINJAK1DD Miningcore fork:

| Coin | Address |
| --- | --- |
| BTC | `bc1q94x9ncw62g09c80yr38jkewyn6cre3h473g54j` |
| ETH | `0x4DE55672F0bBB88882A5a589b320eE40FfbdebF9` |
| DOGE | `DQKEyZ2sTzcCPeeqzP4xUiPHzwtCS9LUTt` |
| ZEC | `t1TbjCnoNdGWnwEt9QqCZvHuG3MsWf4Bj66` |
| XMR | `43iiCs5pjvqbzYDvGSPgwtTdR4E4s996cSBsCSTe5HHbSrzr4HBosKZch8t7Fpg34DL9dNcN22T7H6JWEC23B9iDLAZqQsp` |
| BCH | `bitcoincash:qzyvaurh8vlj22jvyhpdce6ld4lt3zfc3svyt665de` |
| LTC | `ltc1qgnt28drw663gldx76zp3s28xl58wsp0ccv4vxg` |
| KAS | `kaspa:qzdtdjatlzecrt9u4v22p5vgud6w6ylvemly9df6zpu0gp0yks9xxp24q79pu` |
| ETC | `0x331e6c8d7Caae3Dd1136EefF6c828dBDe5ae64F0` |
| FIRO | `aH1tURoFqY1quNraAtceE6YFPv3DLFo8zT` |
| XEL | `xel:gt8m2j4al22k8ecp99uducy84vnhn2nlx6ftxjgw2rfr0hg5n47sqkec7n4` |
| WART | `4701843e274a2a4dfbac59678cb693233274bf5fefcc4e46` |

Always verify the address and network before sending funds. Cryptocurrency transfers cannot be
reversed.

## Licence and upstream credit

Miningcore is distributed under the terms in [LICENSE](LICENSE). This distribution derives from
Miningcore and acknowledges its original maintainers and contributors; consult the upstream history
for earlier work.

Third-party dependencies retain their own licence terms. AutoMapper's licence states that version 16
source and binaries are governed by the
[Reciprocal Public License 1.5](https://github.com/LuckyPennySoftware/AutoMapper/blob/dfa6dd587c5854b4beee5934beb39ba6e9569b84/LICENSE.md),
unless they are used under AutoMapper's commercial licence agreement. Miningcore's licence does not
replace those terms. Review them for your deployment and obtain independent legal advice if needed.
If you use a Lucky Penny licence, follow the
[licence-key configuration guide](docs/lucky-penny-licence.md) for systemd and Docker setup,
verification, rotation and troubleshooting. Do not store a key in `config.json` or source control.
Without a key, AutoMapper logs a warning but does not disable runtime features; operators remain
responsible for complying with the applicable licence terms.

See [Dependency security](docs/dependency-security.md) for NuGet audit policy, the AutoMapper dependency
decision and the documented risk acceptance for the legacy Zcash cryptography dependency.
