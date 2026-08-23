# Miningcore documentation

Start with the guide that matches the task. The root [README](../README.md) introduces Miningcore and
contains the shortest installation path; the documents below hold the operational detail.

If Miningcore is already failing or behaving unexpectedly, start with
[Troubleshooting](troubleshooting.md). It maps symptoms to safe first checks and then links to the
authoritative recovery procedure. Do not begin an accounting incident by editing PostgreSQL,
wallet balances or recovery files.

## Running a pool

| Task | Guide |
| --- | --- |
| Install, upgrade or roll back a release | [Release installation](releases.md) |
| Choose a ready-to-edit pool or relay topology | [Example configurations](../examples/README.md) |
| Configure pools, logging and recovery storage | [Configuration](configuration.md) |
| Operate and monitor a production service | [Operator handbook](operations.md) |
| Diagnose startup, mining, payout or storage problems | [Troubleshooting](troubleshooting.md) |
| Set up, back up, migrate or recover PostgreSQL | [Database and recovery](database.md) |
| Migrate an existing .NET 6 deployment | [.NET 6 to .NET 10 migration](dotnet-6-to-10-migration.md) |

## Features and interfaces

| Task | Guide |
| --- | --- |
| Use REST, WebSocket events, metrics or administration | [API and monitoring](api.md) |
| Provision and rotate administrative credentials | [Administrative API security](admin-api-security.md) |
| Deploy distributed Stratum/recorder roles | [Share relays](share-relays.md) |
| Configure Litecoin–Dogecoin merged mining | [Merged mining](merged-mining-litecoin-dogecoin.md) |
| Review the daemon-backed merged-mining evidence | [Regtest validation record](merged-mining-regtest-validation.md) |
| Review dated production evidence and outstanding gates | [Mainnet validation record](mainnet-validation.md) |

## Dependencies and licensing

- [Dependency security decisions](dependency-security.md)
- [Lucky Penny licence-key configuration](lucky-penny-licence.md)

## Maintainers and release assurance

| Task | Guide |
| --- | --- |
| Build, package and publish a release | [Maintainer release procedure](releases.md#maintainer-release-procedure) |
| Review dependency and licence decisions | [Dependency security](dependency-security.md) |
| Review reproducible daemon-backed evidence | [Regtest validation record](merged-mining-regtest-validation.md) |
| Review dated production evidence | [Mainnet validation record](mainnet-validation.md) |

The machine-readable configuration reference is
[`src/Miningcore/config.schema.json`](../src/Miningcore/config.schema.json), and the maintained
starting configuration is [`config.example.json`](../config.example.json). Release-specific changes
that affect operators are recorded after the installation and upgrade procedures in the
[release guide](releases.md).
