# Miningcore documentation

Start with the guide that matches the task. The root [README](../README.md) introduces Miningcore and
contains the shortest installation path; the documents below hold the operational detail.

## Running a pool

| Task | Guide |
| --- | --- |
| Install, upgrade or roll back a release | [Release installation](releases.md) |
| Configure pools, logging and recovery storage | [Configuration](configuration.md) |
| Operate and monitor a production service | [Operator handbook](operations.md) |
| Set up, back up, migrate or recover PostgreSQL | [Database and recovery](database.md) |
| Migrate an existing .NET 6 deployment | [.NET 6 to .NET 10 migration](dotnet-6-to-10-migration.md) |

## Features and interfaces

| Task | Guide |
| --- | --- |
| Use REST, WebSocket events, metrics or administration | [API and monitoring](api.md) |
| Deploy distributed Stratum/recorder roles | [Share relays](share-relays.md) |
| Configure Litecoin–Dogecoin merged mining | [Merged mining](merged-mining-litecoin-dogecoin.md) |
| Review the daemon-backed merged-mining evidence | [Regtest validation record](merged-mining-regtest-validation.md) |

## Dependencies and licensing

- [Dependency security decisions](dependency-security.md)
- [Lucky Penny licence-key configuration](lucky-penny-licence.md)

The machine-readable configuration reference is
[`src/Miningcore/config.schema.json`](../src/Miningcore/config.schema.json), and the maintained
starting configuration is [`config.example.json`](../config.example.json). Release-specific changes
that affect operators are recorded near the beginning of the [release guide](releases.md).
