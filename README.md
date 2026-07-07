# Miningcore — NINJAK1DD fork

[![.NET](https://github.com/NINJAK1DD/miningcore/actions/workflows/dotnet.yml/badge.svg?branch=dev)](https://github.com/NINJAK1DD/miningcore/actions/workflows/dotnet.yml)
[![License](https://img.shields.io/github/license/NINJAK1DD/miningcore)](LICENSE)

<img src="logo.png" width="150" alt="Miningcore logo">

This repository is the NINJAK1DD Miningcore fork. It builds on the
[upstream Miningcore project](https://github.com/blackmennewstyle/miningcore) and retains credit
to its original authors and contributors while maintaining fork-specific features and deployment
guidance here. The `dev` branch is this fork's primary integration branch.

## Key features

- Litecoin parent-chain and Dogecoin AuxPoW merged mining for SOLO pools.
- Low-latency asynchronous Stratum servers with vardiff and native proof-of-work validation.
- Multiple pools and currencies in one cluster.
- PostgreSQL-backed share, block, statistics and payment processing.
- Database-free share-relay sender nodes with central receiver/recorder deployments.
- REST API, WebSocket events, pool statistics, banning and payment processing.
- Linux production builds and Windows development builds.

The currencies defined by this revision are listed in
[coins.json](src/Miningcore/coins.json).

## Litecoin–Dogecoin merged mining

Merged mining exposes the Litecoin Stratum endpoint to miners and submits qualifying copies of the
same Scrypt proof to Dogecoin. This implementation is SOLO-only: the parent LTC pool and auxiliary
DOGE pool must both be enabled and configured for SOLO processing.

The Litecoin pool references the Dogecoin pool:

```json
"mergedMining": {
  "enabled": true,
  "auxPoolId": "doge-solo",
  "addressParameter": "doge",
  "requireAuxAddress": true,
  "auxiliaryTemplatePollTimeoutMs": 500
}
```

Miners connect only to Litecoin:

```text
Username: <LTC address>.<worker>
Password: d=<difficulty>;doge=<DOGE address>
```

Read the [complete Litecoin–Dogecoin merged-mining guide](docs/merged-mining-litecoin-dogecoin.md)
before enabling the feature. It covers daemon setup, payout attribution, migrations, failure modes,
relay compatibility and the required pre-production regtest.

## Deployment models

| Role | PostgreSQL | Local payout manager | Merged-mining schema preflight |
| --- | --- | --- | --- |
| Direct pool/recorder | Required | Required for merged mining | Required |
| Database-free relay sender | Not required | Disabled | Skipped |
| Relay sender selected as payout node | Required | Allowed when configured; avoid duplicate processors | Required if it reconciles merged mining |
| Central relay receiver/recorder | Required | Required for merged mining unless another database-connected node is explicitly the sole payout/reconciliation owner | Required when it reconciles merged mining |

For merged mining, cluster-level `paymentProcessing.enabled` must be true on the node that owns
reconciliation and payouts. Exactly one database-connected node should own that role for a
database/pool set. That node must include the local `mergedMining` configuration for the merged pool,
all participating nodes must use the same PostgreSQL database, and enabling payment processing on both
the relay sender and receiver can result in concurrent balance and payout processing.

Share relay uses ZeroMQ PUB/SUB and is not an acknowledged durable queue. A disconnected receiver or
process failure can lose an accepted block event. Production deployments requiring stronger financial
durability should add a synchronous repository/outbox write or an acknowledged durable transport.

## Build and installation

Clone this fork and use its integration branch:

```console
git clone https://github.com/NINJAK1DD/miningcore.git
cd miningcore
git checkout dev
```

### Recommended Linux build

Debian 12 is the clearest maintained script path in this repository:

```console
./build-debian-12.sh
```

The GitHub Actions [`.NET` workflow](.github/workflows/dotnet.yml) is the authoritative automated
build-and-test environment. The project currently targets the unsupported .NET 6 runtime; upgrade to a
supported .NET release before treating this fork as production-hardened. Modernising the target
framework is tracked as separate work.

### Docker

Build on hardware compatible with the production host because native hashing libraries may be
optimised for the builder's CPU features:

```console
docker build -t miningcore:local .
docker run -d \
  --name miningcore \
  --restart unless-stopped \
  -p 4000:4000 \
  -v "$(pwd)/config.json:/app/config.json:ro" \
  miningcore:local
```

Add the Stratum ports used by your pool configuration to the `docker run` command.

### Windows development

Windows is supported for development, not recommended for production pool operation. Install the
[.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0), then run:

```dosbatch
build-windows.bat
```

Visual Studio 2022 can open `Miningcore.sln` directly.

## Database setup and upgrades

Direct nodes, relay receiver/recorders and any node running payment processing require PostgreSQL.
Database-free relay senders are the exception. The schema may remain compatible with older PostgreSQL
versions, but production deployments should use a currently supported PostgreSQL major release; for a
new public pool, PostgreSQL 15 or later is a more defensible baseline than PostgreSQL 10.

Create a role and database:

```sql
CREATE ROLE miningcore WITH LOGIN ENCRYPTED PASSWORD 'replace-with-a-strong-password';
CREATE DATABASE miningcore OWNER miningcore;
```

For a new database, import the complete current schema. It already contains all merged-mining
idempotency indexes:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/createdb.sql
```

For an existing database created before LTC/DOGE merged mining, back it up and apply the transactional
upgrade before enabling the feature:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f src/Miningcore/Persistence/Postgres/Scripts/add_auxpow_block_idempotency.sql
```

The migration stops rather than guessing if legacy or duplicate claimant rows require manual review.
Startup also verifies the required unique partial indexes and refuses merged mining when they are
missing or malformed.

The optional PostgreSQL 11 partitioning appendix is available at
[createdb_postgresql_11_appendix.sql](src/Miningcore/Persistence/Postgres/Scripts/createdb_postgresql_11_appendix.sql).
It deletes/rebuilds the shares table, so read the script and take a verified backup before using it.

## Configuration and running

Create `config.json` for the pools and services you intend to run. The configuration format is inherited
from Miningcore; the [upstream configuration wiki](https://github.com/oliverw/miningcore/wiki/Configuration)
is useful background, but fork-specific behavior is documented in this repository.

Start a published build with:

```console
Miningcore -c config.json
```

The REST API listens on port 4000 by default. The
[upstream API reference](https://github.com/oliverw/miningcore/wiki/API) describes the inherited API;
always compare it with this revision when operating fork-specific features.

## Testing and production status

Run the automated test project with:

```console
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj
```

Automated tests cover AuxPoW serialization, proof attribution, reconciliation, persistence SQL,
deployment validation and regressions. They do not replace live integration testing.

Before mainnet funds are enabled, complete the documented tests with real `litecoind`, `dogecoind` and
PostgreSQL. Required scenarios include height-decreasing Litecoin reorganisations, competing parent
proofs, response loss, duplicate submissions, Dogecoin reorganisations, parent-only/DOGE-only/dual-target
solutions, Litecoin MWEB templates, payout maturity, wallet credit and concurrent PostgreSQL claim
promotion.

## Security and durability

- Isolate daemon and wallet RPC endpoints from untrusted networks; use firewalling and TLS-capable
  reverse proxies where appropriate.
- Keep hot-wallet balances limited and maintain tested wallet/database backups.
- Protect Stratum, API and relay endpoints with network controls appropriate to the deployment.
- Treat ZeroMQ share relay as unacknowledged transport, not a financial ledger.
- Apply database migrations before deploying code that depends on them.
- Monitor reconciliation warnings, uncertain claims, payout processing and daemon health.

## Contributions and support

Submit changes to this repository as pull requests targeting
[the `dev` branch](https://github.com/NINJAK1DD/miningcore/tree/dev). Use this repository's
[issue tracker](https://github.com/NINJAK1DD/miningcore/issues) for reproducible fork-specific bugs.

The [upstream repository](https://github.com/blackmennewstyle/miningcore), its discussions and any
upstream commercial services are maintained separately and should not be interpreted as support
offered by this fork.

## Licence and upstream credit

Miningcore is distributed under the terms in [LICENSE](LICENSE). This fork derives from Miningcore and
acknowledges its original maintainers and contributors; consult the upstream history for earlier work.
