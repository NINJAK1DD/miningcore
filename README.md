# BTCPool.co.uk Miningcore

[![.NET](https://github.com/NINJAK1DD/miningcore/actions/workflows/dotnet.yml/badge.svg?branch=dev)](https://github.com/NINJAK1DD/miningcore/actions/workflows/dotnet.yml)
[![License](https://img.shields.io/github/license/NINJAK1DD/miningcore)](LICENSE)

<img src="logo.png" width="150" alt="Miningcore logo">

This is the Miningcore distribution maintained for [BTCPool.co.uk](https://btcpool.co.uk)
SOLO mining service. The source repository is hosted by `NINJAK1DD`, and the `dev` branch is its
primary integration branch. It builds on the
[upstream Miningcore project](https://github.com/blackmennewstyle/miningcore) and retains credit to
the original authors and contributors.

> **Production status:** the software is usable for development and regtest, but this branch still
> targets the unsupported .NET 6 runtime. Read [Production operation](#production-operation) and the
> live [merged-mining validation record](docs/merged-mining-regtest-validation.md) before using real
> funds.

## Features

- High-performance asynchronous Stratum servers.
- Multiple pools and currencies in one cluster; see this revision's [coin definitions](src/Miningcore/coins.json).
- Native proof-of-work validation with fixed difficulty and variable difficulty (vardiff).
- SOLO, PPLNS and PROP payout schemes where supported by the selected coin.
- PostgreSQL-backed shares, blocks, balances, statistics and payment processing.
- Share relays for distributed pool deployments.
- REST API, WebSocket notifications and Prometheus-compatible metrics.
- Integrated banning, TLS options, administrative notifications and payment processing.
- Litecoin parent-chain and Dogecoin AuxPoW merged mining for SOLO pools.
- Linux, Windows-development and Docker build paths.

## Quick start

The shortest route to a local build is:

```console
git clone https://github.com/NINJAK1DD/miningcore.git
cd miningcore
git checkout dev
./build-debian-12.sh
cp config.example.json build/config.json
```

Next, install PostgreSQL, create the database, replace every `CHANGE_ME` value in `config.json`, and
start Miningcore from the build directory:

```console
cd build
./Miningcore -c config.json
```

The example deliberately will not start until its placeholder wallet addresses and passwords are
replaced. The following sections walk through each step.

## Litecoin–Dogecoin merged mining

Merged mining exposes the Litecoin Stratum endpoint to miners and submits qualifying copies of the
same Scrypt proof to Dogecoin. It is **SOLO-only**: both pools must be enabled, use `SOLO` payment
processing, and have their own payout wallet address.

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

### Debian and Ubuntu

Run the script matching the installed operating system from the repository root. The script installs
the native build dependencies and .NET SDK, then publishes Miningcore into `build/`.

| Operating system | Command | Guidance |
| --- | --- | --- |
| Debian 12 | `./build-debian-12.sh` | **Recommended script path** |
| Ubuntu 22.04 LTS | `./build-ubuntu-22.04.sh` | Recommended Ubuntu script path |
| Debian 11 | `./build-debian-11.sh` | Older compatibility path |
| Ubuntu 20.04 LTS | `./build-ubuntu-20.04.sh` | Older compatibility path |
| Ubuntu 21.04 | `./build-ubuntu-21.04.sh` | Historical/EOL; do not use for production |

For example:

```console
chmod +x build-debian-12.sh
./build-debian-12.sh
ls build/Miningcore
```

These scripts reproduce the repository's existing .NET 6 build and are not a statement that .NET 6
or an end-of-life operating system is safe for a new production deployment. GitHub Actions
([workflow source](.github/workflows/dotnet.yml)) is the authoritative automated build-and-test path.

### Windows development

Windows is supported for development and testing, not recommended for hosting a production pool.

1. Install the [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0).
2. Optionally install [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) with the
   **.NET desktop build tools** and **Desktop development with C++** workloads.
3. Clone the repository and open [Miningcore.sln](src/Miningcore.sln), or run:

```dosbatch
build-windows.bat
```

The published files are written to `build`.

### Docker Engine

Install [Docker Engine for your Linux distribution](https://docs.docker.com/engine/install/) and
confirm it works before building Miningcore:

```console
sudo docker run --rm hello-world
git clone https://github.com/NINJAK1DD/miningcore.git
cd miningcore
git checkout dev
sudo docker build -t btcpool-miningcore:local .
cp config.example.json config.json
```

Edit `config.json`, then run the container. This example publishes the API and the merged-mining LTC
Stratum port; publish any additional ports used by your configuration. The example keeps the separate
DOGE Stratum listener disabled because merged miners connect to LTC:

```console
sudo docker run -d \
  --name miningcore \
  --restart unless-stopped \
  -p 4000:4000 \
  -p 3032:3032 \
  -v "$(pwd)/config.json:/app/config.json:ro" \
  btcpool-miningcore:local
```

Useful management commands:

```console
sudo docker logs -f miningcore
sudo docker restart miningcore
sudo docker stop miningcore
sudo docker rm miningcore
```

The container must be able to reach PostgreSQL and every coin daemon. `127.0.0.1` inside a container
means the container itself, not the Docker host. Build on hardware compatible with the production
host because native hashing libraries can be affected by CPU architecture and features.

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
pg_dump -h 127.0.0.1 -U miningcore -Fc miningcore > miningcore-backup.dump
```

> [!IMPORTANT]
> This revision is a breaking database upgrade for every deployment that enables payment
> processing, even when LTC/DOGE merged mining is not enabled. Stop Miningcore and apply
> `add_payout_manager_ownership.sql` before starting the upgraded binary. Recovery-only nodes that
> use `-rs` require the same migration. Missing ownership/idempotency schema fails startup with the
> migration filename instead of running payouts without protection.

For an existing database, stop writers and payout managers before applying the migrations required by
this revision. The [database and upgrade guide](docs/database.md) gives the exact commands, restore
procedure, merged-mining indexes, payout-manager ownership rules and optional advanced partitioning.

## Configuration

Copy [config.example.json](config.example.json) to `config.json`. It is a JSON-with-comments example
covering the common cluster, API, PostgreSQL, statistics, banning, payment, daemon, Stratum, vardiff
and LTC/DOGE merged-mining options. Miningcore accepts comments; ordinary strict-JSON tools may not.

```console
cp config.example.json build/config.json
```

Replace all `CHANGE_ME` values and remove pools or services you do not intend to run. The
[configuration guide](docs/configuration.md) explains the main sections and miner login formats. The
machine-readable [configuration schema](src/Miningcore/config.schema.json) is the exhaustive option
reference, including less common coin-specific extension fields.

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

The API is enabled in the example on port `4000`. Common endpoints include:

```text
GET /api/health-check
GET /api/pools
GET /api/pools/{poolId}
GET /api/pools/{poolId}/blocks
GET /api/pools/{poolId}/miners/{address}
GET /api/blocks
```

The fork now has its own [API guide](docs/api.md), derived from this revision's controllers, rather
than relying solely on an older upstream wiki. It includes v2 routes, WebSocket notifications,
metrics, rate limiting, admin-port isolation and reverse-proxy guidance.

Miningcore supplies an API, not a bundled public website. A community project such as
[btclinux/Miningcore.WebUI](https://github.com/btclinux/Miningcore.WebUI) can be used as a starting
point, but it is not maintained, audited or endorsed by this project, has not received a code push
since December 2023, and GitHub does not currently detect a licence for it. Review its code and legal
terms, update its API assumptions, and deploy it behind your own HTTPS reverse proxy before exposing
it publicly. BTCPool.co.uk uses its own operational choices; this reference is not a dependency.

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
- **.NET 6 is out of support.** Framework modernisation is intentionally deferred to a separate PR.
- **Coin support is not identical.** Read the coin definition and daemon documentation before
  enabling a currency. Some families require extra native files or configuration.
- **CryptoNote/RandomX coins** can require significant memory per configured VM and CPU-specific
  RandomX flags; validate settings on the actual host.
- **Equihash-family pools** may require a shielded `z-address` depending on the coin and payout path.
- **Vertcoin/Verthash** requires the correct `verthash.dat` file; configure `vertHashDataFile` when it
  is not in the working directory.
- **Wallet RPC is financially sensitive.** Never expose daemon or wallet RPC to the public internet.
- **Share relay is not a durable queue.** ZeroMQ PUB/SUB does not acknowledge ordinary shares; merged
  block candidates use additional synchronous persistence. The physical Windows/WSL sender-to-
  receiver route has passed interruption and reconnect testing, but a production relay deployment
  must still accept that ordinary shares sent while the receiver is unreachable are not replayed.
- **Block-submission timing is durability-first.** After local proof validation, the manager owns
  candidate delivery independently of miner EOF or TCP reset. Its ten-second merged-mining deadline
  covers daemon submission and attribution, not PostgreSQL retries or write-through recovery-journal
  I/O. A block candidate can take longer to acknowledge during storage failure because client
  cancellation is not allowed to abandon financially significant delivery or recording. Host
  shutdown signals mining directly from `ApplicationStopping`, before sequential hosted-service
  shutdown begins. The mining coordinator is also registered after the optional API web host, so it
  is stopped and awaited before Kestrel can consume the shared budget. It quiesces new merged
  submissions, waits for proof validations already underway to hand off any candidate, and then
  drains candidate delivery and persistence. Miningcore explicitly reserves 45 seconds for graceful
  host shutdown. Once quiescing starts, candidate persistence skips the ordinary 2/4/8-second retry
  delays, grants the active PostgreSQL attempt at most five seconds, then writes and force-flushes the
  recovery journal. Ordinary-share and candidate persistence use the same recorder singleton and a
  canonical-filename journal lock. If an unexpected candidate database failure requires emergency
  journalling, Miningcore stops the cluster because the accounting pipeline is no longer trusted. If
  both PostgreSQL and the journal fail, the cluster also stops with exit status 1 instead of leaving
  other miners online without durable block accounting. Configure the service manager's stop timeout
  above 45 seconds; the supplied systemd example uses 60 seconds.

## Production operation

Before advertising a public pool:

- Run Miningcore on a maintained Linux release and plan migration from .NET 6.
- Isolate daemon, wallet, PostgreSQL, admin API and relay ports with host/network firewalls.
- Put the public API and website behind an HTTPS reverse proxy; do not expose the admin API port.
- Keep hot-wallet balances limited and test encrypted wallet, database and configuration backups.
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

## Contributions and support

Submit changes as pull requests targeting [the `dev` branch](https://github.com/NINJAK1DD/miningcore/tree/dev).
Use this repository's [issue tracker](https://github.com/NINJAK1DD/miningcore/issues) for reproducible
fork-specific bugs. Operational information for the hosted pool belongs on
[BTCPool.co.uk](https://btcpool.co.uk), not in GitHub issues.

The [upstream repository](https://github.com/blackmennewstyle/miningcore), its discussions and any
upstream commercial services are maintained separately and are not support offered by BTCPool.co.uk.

## Licence and upstream credit

Miningcore is distributed under the terms in [LICENSE](LICENSE). This distribution derives from
Miningcore and acknowledges its original maintainers and contributors; consult the upstream history
for earlier work.
