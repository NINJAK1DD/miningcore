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
- Opt-in Bitcoin direct-coinbase SOLO, paying the authorized miner and positive pool fee/donation
  recipients in separate outputs of the accepted block instead of creating a custodial Miningcore
  balance.
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

This path installs the verified prebuilt release on a new **Ubuntu 26.04 x64** host, creates
PostgreSQL, prepares the configuration, and runs Miningcore under systemd. Run each block only after
the preceding check succeeds. Ubuntu 22.04 uses its separately built compatibility archive and
different runtime packages; Ubuntu 24.04 is a source-build target. Use the
[release guide](docs/releases.md) for those paths or for an upgrade/rollback.

> [!CAUTION]
> This branch currently pins the `v0.3.0-rc.1` release candidate. Test release candidates on
> regtest or a controlled staging pool before relying on them for real funds. Operators who want
> the latest stable release should select it from the
> [releases page](https://github.com/NINJAK1DD/miningcore/releases) and substitute that tag in every
> command below.

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
export MININGCORE_VERSION=v0.3.0-rc.1
export MININGCORE_UBUNTU=26.04
MININGCORE_QUICKSTART_READY=
download_dir="$(mktemp -d "${TMPDIR:-/tmp}/miningcore-release.XXXXXXXX")"
archive_name="miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}.tar.gz"
release_url="https://github.com/NINJAK1DD/miningcore/releases/download/${MININGCORE_VERSION}"
curl --fail --location --output "$download_dir/$archive_name" \
  "$release_url/$archive_name"
curl --fail --location --output "$download_dir/SHA256SUMS" \
  "$release_url/SHA256SUMS"
if (cd "$download_dir" && \
    sha256sum --ignore-missing --check --strict SHA256SUMS); then
  export MININGCORE_QUICKSTART_READY=1
  echo "READY: $archive_name is verified"
else
  echo "STOP: release download or checksum verification failed" >&2
fi
```

Do not continue unless the checksum command reports the selected archive as `OK`. If the GitHub CLI
is installed, also verify the release provenance:

```console
if [ "${MININGCORE_QUICKSTART_READY:-}" = 1 ]; then
  gh attestation verify "$download_dir/$archive_name" --repo NINJAK1DD/miningcore
else
  echo "STOP: no release archive passed checksum verification" >&2
fi
```

### 3. Install the versioned application

```console
MININGCORE_INSTALL_READY=
if [ "${MININGCORE_QUICKSTART_READY:-}" = 1 ]; then
  release_dir="/opt/miningcore-${MININGCORE_VERSION}-linux-x64-ubuntu-${MININGCORE_UBUNTU}"
  install_miningcore_release() {
    { id -u miningcore >/dev/null 2>&1 ||
      sudo useradd --system --home-dir /var/lib/miningcore \
        --shell /usr/sbin/nologin miningcore; } || return
    sudo mkdir -p /opt || return
    sudo tar -xzf "$download_dir/$archive_name" -C /opt || return
    test -d "$release_dir" || return
    sudo install -d -m 0750 -o root -g miningcore /etc/miningcore || return
    sudo install -d -m 0750 -o miningcore -g miningcore \
      /var/lib/miningcore /var/log/miningcore || return
    if [ ! -e /etc/miningcore/config.json ]; then
      sudo install -m 0640 -o root -g miningcore \
        "$release_dir/config.example.json" /etc/miningcore/config.json || return
    else
      echo "Keeping existing /etc/miningcore/config.json"
    fi
    cat "$release_dir/BUILD-INFO" || return
    LD_LIBRARY_PATH="$release_dir" "$release_dir/Miningcore" --version || return
    sudo ln -sfnT "$release_dir" /opt/miningcore || return
  }

  if install_miningcore_release; then
    MININGCORE_QUICKSTART_READY=
    export MININGCORE_INSTALL_READY=1
    echo "READY: installed $release_dir and updated /opt/miningcore"
  else
    echo "STOP: installation failed; /opt/miningcore was not changed" >&2
  fi
else
  echo "STOP: no verified release archive is available to install" >&2
fi
```

The version output must match the selected tag and include its source commit. Keep
`/opt/miningcore` as the stable symlink; future upgrades install another immutable versioned
directory before changing that link. Continue only after the block prints `READY` and exports
`MININGCORE_INSTALL_READY=1`; the verified-download latch is consumed after a successful install.

### 4. Create PostgreSQL and load the schema

Create a dedicated role without putting its password on the command line:

```console
MININGCORE_DATABASE_READY=
role_exists=
database_exists=
if role_exists="$(sudo -u postgres psql -X -A -t -v ON_ERROR_STOP=1 \
     -d postgres -c "SELECT 1 FROM pg_roles WHERE rolname = 'miningcore';")" &&
   database_exists="$(sudo -u postgres psql -X -A -t -v ON_ERROR_STOP=1 \
     -d postgres -c "SELECT 1 FROM pg_database WHERE datname = 'miningcore';")"; then
  if [ "$role_exists" = 1 ] || [ "$database_exists" = 1 ]; then
    echo "STOP: the miningcore role or database already exists; use the upgrade runbook" >&2
  elif sudo -u postgres createuser --pwprompt miningcore &&
       sudo -u postgres createdb --owner=miningcore miningcore &&
       sudo -u postgres psql --single-transaction -v ON_ERROR_STOP=1 \
         -d miningcore -f /opt/miningcore/migrations/createdb.sql &&
       psql -h 127.0.0.1 -U miningcore -d miningcore \
         -c 'SELECT current_database(), current_user;'; then
    export MININGCORE_DATABASE_READY=1
    echo "READY: created and verified the miningcore database"
  else
    echo "STOP: database provisioning failed; inspect PostgreSQL before retrying" >&2
  fi
else
  echo "STOP: unable to inspect existing PostgreSQL roles and databases" >&2
fi
```

On a successful fresh provision, the verification command prompts for the new password, must report
database/user `miningcore`, and the block exports `MININGCORE_DATABASE_READY=1`. Existing database
operators should stop here and follow the
[upgrade and migration runbook](docs/database.md#upgrade-an-existing-database) instead of running
the new-database schema over live data.

### 5. Choose and edit a configuration

The installed `config.example.json` is the fully annotated reference. Smaller reviewed pool,
multi-coin, merged-mining and relay files are under `/opt/miningcore/examples/`; copy one over the
starter only when it matches the intended topology.

#### Optional: enable Bitcoin direct-coinbase SOLO

Skip this subsection for conventional custodial SOLO, PPS, PROP or PPLNS operation. It requires a
Miningcore binary that implements direct settlement and its matching database schema—on this
branch, that means `v0.3.0-rc.1`. Direct coinbase settlement is an explicit, BTC-only option in
which the authorized miner address and each positive pool fee/donation recipient are paid by
separate outputs in the accepted block.

If you substituted the stable `v0.2.1` release in this quick start, skip this entire subsection:
that binary does not implement `soloCoinbasePayout`. Upgrade the binary and database before adding
or enabling the setting. Only a fresh database created from `v0.3.0-rc.1` has the required schema
from `createdb.sql`. For a database created by `v0.2.1` or earlier—or any pre-PR #135 build—keep
`soloCoinbasePayout` disabled until the verified candidate migration has completed; use the
[direct-SOLO database migration](docs/bitcoin-direct-solo.md#database-migration), not
`createdb.sql` and not a migration beneath the old `/opt/miningcore` symlink.

If selecting direct settlement, install the reviewed
[`bitcoin_direct_solo_pool.json`](examples/bitcoin_direct_solo_pool.json) contract before editing:

```console
sudo install -m 0640 -o root -g miningcore \
  /opt/miningcore/examples/bitcoin_direct_solo_pool.json /etc/miningcore/config.json
```

While editing below, keep both cluster- and pool-level payment processing enabled, retain
`payoutScheme: "SOLO"`, replace the pool wallet, daemon credentials and positive recipient address,
and explicitly set `soloCoinbasePayout: true`. Miners must authorize with a valid network-matching
`BITCOIN_ADDRESS.worker` username. Complete the [direct-SOLO guide](docs/bitcoin-direct-solo.md),
including its regtest/preflight procedure, before admitting production miners. The option remains
off by default for every existing pool.

After choosing the configuration, edit the protected file:

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

After choosing an example and completing any optional direct-SOLO changes, run this final
fail-closed check. Continue only when it prints `READY`; a placeholder match or an inspection error
returns a nonzero status:

```console
quickstart_placeholder_status=0
sudo grep -n 'CHANGE_ME' /etc/miningcore/config.json || quickstart_placeholder_status=$?
case "$quickstart_placeholder_status" in
  0) echo 'STOP: replace every CHANGE_ME value before starting Miningcore' >&2; false ;;
  1) echo 'READY: no CHANGE_ME placeholders remain' ;;
  *) echo 'STOP: could not inspect /etc/miningcore/config.json' >&2; false ;;
esac
```

### 6. Install, secure and synchronize the coin daemons

Miningcore does not install or manage the full nodes and payout wallets named by `pools[].daemons`.
Install each daemon from its authoritative project, verify its release, bind RPC to a private
interface, use a unique strong RPC credential, and allow the node to synchronize fully. Create the
pool payout wallet, encrypt and back it up, and test the documented restore procedure away from
production. Do not expose daemon RPC or wallet RPC to the internet.

Coin-specific RPC, wallet and extension requirements vary. Start with the chosen file in the
[example index](examples/README.md), then check the matching definition in
the installed `/opt/miningcore/coins.json` file (repository
[source](src/Miningcore/coins.json)) and the daemon's pinned/released documentation. Miningcore must
not be started until every enabled pool's daemon and wallet endpoint is reachable using the
credentials in `/etc/miningcore/config.json`.

### 7. Optional: partition the `shares` table

Skip this for a first or small pool. List partitioning is an advanced multipool optimization. On a
new, still-empty database, it may be enabled before the first Miningcore start; on any database that
already contains shares, use the complete backup/restore procedure in
[Advanced share-table partitioning](docs/database.md#advanced-share-table-partitioning).

The appendix deletes and rebuilds `shares`, so first preserve even the empty baseline:

```console
umask 077
MININGCORE_PARTITION_READY=
partition_backup="$HOME/miningcore-before-partition.dump"
share_count=
partitioned_share_table_count=
if sudo -u postgres pg_dump -Fc -d miningcore > "$partition_backup" &&
   pg_restore --list "$partition_backup" > /dev/null &&
   share_count="$(sudo -u postgres psql -X -A -t -v ON_ERROR_STOP=1 \
     -d miningcore -c 'SELECT count(*) FROM public.shares;')" &&
   partitioned_share_table_count="$(sudo -u postgres psql -X -A -t \
     -v ON_ERROR_STOP=1 -d miningcore \
     -c "SELECT count(*) FROM pg_partitioned_table WHERE partrelid = \
       'public.shares'::regclass;")"; then
  if [ "$share_count" != 0 ]; then
    echo "STOP: shares is not empty; use the full partition migration runbook" >&2
  elif [ "$partitioned_share_table_count" != 0 ]; then
    echo "STOP: shares is already partitioned; keep its current layout or use the full" \
      "partition migration runbook" >&2
  elif sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
       -f /opt/miningcore/migrations/createdb_postgresql_11_appendix.sql; then
    export MININGCORE_PARTITION_READY=1
    echo "READY: rebuilt the empty shares table as a partitioned table"
  else
    echo "STOP: partition appendix failed; restore or investigate before continuing" >&2
  fi
else
  echo "STOP: backup or shares-table inspection failed; appendix not run" >&2
fi
```

Continue with partition creation only after the block prints `READY` and exports
`MININGCORE_PARTITION_READY=1`. Any backup, validation, table-inspection or appendix failure leaves
that latch empty and must be investigated before retrying. Rerunning this conversion against an
already partitioned `shares` table is refused so its existing partition layout remains intact.

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
sudo install -m 0600 -o root -g root /dev/null \
  /etc/miningcore/miningcore.env
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env >/dev/null
unset token
sudo cp /opt/miningcore/systemd/miningcore.service \
  /etc/systemd/system/miningcore.service
sudo systemctl daemon-reload
sudo systemctl enable --now miningcore
sudo systemctl status miningcore --no-pager -l
```

The unit runs as the unprivileged `miningcore` account, creates persistent state/log directories,
allows Miningcore's bounded clean shutdown to finish, and prevents an unsafe automatic restart
after dual persistence failure (exit status 74).

#### Review AutoMapper licensing and configure an applicable Lucky Penny key

AutoMapper 16 is dual-licensed under RPL-1.5 or Lucky Penny commercial terms, including a free
Community tier for qualifying users. Determine and document the applicable path for your deployment;
if that path provides a key, configure it now in a separate root-only environment file and systemd
drop-in. Do not place the key in `config.json`, the packaged unit, shell history or source control.
Obtain a key through the official
[AutoMapper licensing and pricing page](https://automapper.io/) and
[Lucky Penny registration](https://luckypennysoftware.com/Identity/Account/Register), then follow the
[Lucky Penny licence-key guide](docs/lucky-penny-licence.md) for choosing the correct environment
variable, secure installation, validation, rotation and Docker instructions. That guide helps you
configure a key; it does not determine which licence terms apply to your deployment.

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
| Review AutoMapper licensing or configure a Lucky Penny key | [Lucky Penny licence-key guide](docs/lucky-penny-licence.md) |
| Deploy distributed Stratum/recorder roles | [Share-relay guide](docs/share-relays.md) |
| Enable direct Bitcoin-family PPS | [PPS operator guide](docs/pps.md) |
| Pay Bitcoin SOLO miners directly in the coinbase | [Bitcoin direct-SOLO guide](docs/bitcoin-direct-solo.md) |
| Migrate a v0.2.1-or-earlier/pre-PR #135 database before enabling direct Bitcoin SOLO | [Direct-SOLO database migration](docs/bitcoin-direct-solo.md#database-migration) |
| Validate a new deployment before miners | [Operator preflight](docs/operations.md#before-accepting-miners) |
| Migrate an existing .NET 6 deployment | [.NET 6 to .NET 10 migration guide](docs/dotnet-6-to-10-migration.md) |
| Enable Litecoin–Dogecoin merged mining | [Merged-mining guide](docs/merged-mining-litecoin-dogecoin.md) |
| Configure and commission DigiByte direct mining | [DigiByte guide](docs/digibyte.md) |
| Review newly added Scrypt daemon contracts | [Scrypt coin definitions](docs/scrypt-coin-definitions.md) |
| Review Bitcoin-family BIP310 mask safety | [Version rolling](docs/version-rolling.md) |

The complete [documentation index](docs/README.md) also links dependency, licensing and validation
references.

## Bitcoin direct-coinbase SOLO

Canonical Bitcoin SOLO pools can opt into non-custodial coinbase settlement. Each authorized
`address.worker` receives destination-specific SV1 work whose coinbase pays the miner directly and
places each positive pool fee/donation in a separate output. The option defaults off; BTC-only,
SOLO, database, topology and address contracts fail closed before work begins. Apply the additive
migration and complete the [Bitcoin direct-SOLO guide](docs/bitcoin-direct-solo.md) before enabling
the [copy-first example](examples/bitcoin_direct_solo_pool.json).

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
2. Install [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) with the
   **Desktop development with C++** workload and the v143 toolset. Windows builds compile the
   pinned Odocrypt runtime from source and fail closed when this toolchain is unavailable.
3. Clone the repository and open [Miningcore.sln](src/Miningcore.sln), or run:

```dosbatch
build-windows.bat
```

The published files are written to `build`.

For managed-only development on a machine without the C++ workload, `dotnet build` and unrelated
tests may use `-p:BuildOdoCryptWindows=false`. That development-only opt-out omits Odocrypt from the
output: Odocrypt tests and runtime use will fail, and Windows publish always requires the verified
source build. For local toolchain-compatibility testing, an installed alternative can be selected
with `MININGCORE_WINDOWS_PLATFORM_TOOLSET`; setting it forces a native rebuild so the selected
toolset is actually exercised. Release CI remains pinned to v143.

### Docker Engine

Install [Docker Engine for your Linux distribution](https://docs.docker.com/engine/install/) and
confirm it works:

```console
sudo docker run --rm hello-world
MININGCORE_VERSION=v0.3.0-rc.1  # Replace with the release you selected.
sudo docker pull ghcr.io/ninjak1dd/miningcore:${MININGCORE_VERSION}
```

Pin a published version rather than copying the example version indefinitely. Copy and edit the
configuration, then run the image. This example uses a fixed Docker bridge gateway, publishes the
public API, binds the admin and metrics ports to host loopback, and publishes the merged-mining LTC
Stratum port. Publish every additional port used by your configuration:

```console
MININGCORE_VERSION=v0.3.0-rc.1  # Replace with the release you selected.
sudo mkdir -p /etc/miningcore /var/lib/miningcore
sudo curl -fL \
  https://raw.githubusercontent.com/NINJAK1DD/miningcore/${MININGCORE_VERSION}/config.example.json \
  -o /etc/miningcore/config.json
sudo chown root:10001 /etc/miningcore/config.json
sudo chmod 0640 /etc/miningcore/config.json
sudo install -m 0600 -o root -g root /dev/null \
  /etc/miningcore/miningcore.env
token="$(openssl rand -hex 32)"
printf 'MININGCORE_ADMIN_API_TOKEN=%s\n' "$token" |
  sudo tee /etc/miningcore/miningcore.env >/dev/null
unset token
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

## Database, configuration and manual source runs

The [Quick start](#quick-start) is the single copy-paste path for a new prebuilt installation. To
avoid competing procedures, detailed database creation, backup, migration, partitioning and
recovery commands live only in the task-specific runbooks:

| Task | Authoritative procedure |
| --- | --- |
| Create a new PostgreSQL database | [Quick start: create PostgreSQL](#4-create-postgresql-and-load-the-schema) |
| Upgrade an existing release and database | [Release upgrade or rollback](docs/releases.md#upgrade-or-roll-back) |
| Enable pooled accounting or PPS | [PPS database prerequisites](docs/pps.md#database-prerequisites) |
| Enable direct-coinbase SOLO on a v0.2.1-or-earlier/pre-PR #135 database | [Direct-SOLO database migration](docs/bitcoin-direct-solo.md#database-migration) |
| Back up, inspect or recover PostgreSQL | [Database and recovery guide](docs/database.md) |
| Partition the `shares` table | [Advanced partitioning](docs/database.md#advanced-share-table-partitioning) |

Never run `createdb.sql` over an existing database, run release migrations through the old active
symlink, or edit balances, blocks or payments manually. Stop every writer named by the upgrade
runbook and prove the backup before a schema change.

For a source build or development session, copy the annotated configuration into the publish
directory and open it for editing:

```console
cp config.example.json build/config.json
editor build/config.json
```

Replace every `CHANGE_ME` value and remove pools or services you do not intend to run. Save the
file, then run this fail-closed placeholder check:

```console
source_placeholder_status=0
grep -n 'CHANGE_ME' build/config.json || source_placeholder_status=$?
case "$source_placeholder_status" in
  0) echo 'STOP: replace every CHANGE_ME value before starting Miningcore' >&2; false ;;
  1) echo 'READY: no CHANGE_ME placeholders remain' ;;
  *) echo 'STOP: could not inspect build/config.json' >&2; false ;;
esac
```

Only after the check prints `READY`, start the published binary:

```console
cd build
./Miningcore -c config.json
```

Miningcore accepts comments in configuration files, while ordinary strict-JSON tools may not. The
[example index](examples/README.md), [configuration guide](docs/configuration.md) and
[coin-family extension guidance](docs/configuration.md#coin-specific-extension-fields) define the
supported starting points. Keep the first run interactive, verify the local health and pool APIs,
then install a production layout before unattended operation. The supplied unit expects
`/opt/miningcore/Miningcore.dll`, `/etc/miningcore/config.json` and the dedicated `miningcore`
account; it does not run the development `build/` layout unchanged. Follow
[quick-start step 8](#8-install-and-start-the-systemd-service) or the
[release service procedure](docs/releases.md#install-the-systemd-service), or create an equivalent
service with paths and an account appropriate to your installation. Do not host a production pool
in `screen` or an interactive SSH session.

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
