# Persistent Ubuntu test lab

The documented local lab is WSL2 `Ubuntu` (Ubuntu 26.04.1), running as the ordinary
`ubuntu` user. Keep Linux source and build outputs separate from Windows outputs.
The lab has .NET SDK 10.0.114/runtime 10.0.14, native build prerequisites (including
Rust/Cargo), and Bitcoin Core 31.1, Litecoin Core 0.21.5.8 and Dogecoin Core 1.14.9
at `/opt/miningcore-test-lab/bin/{bitcoind,litecoind,dogecoind}`. PostgreSQL server
binaries must be installed under `/usr/lib/postgresql/*/bin` (or selected with
`MININGCORE_TEST_POSTGRES_BIN`).

## Run the suite

Validate an exact committed revision in a separate Linux checkout. From the source
checkout in PowerShell, export a bundle (including Git history for GitVersion):

```powershell
git bundle create build/test-lab/miningcore.bundle --all
wsl -d Ubuntu --exec git clone /mnt/c/path/to/miningcore/build/test-lab/miningcore.bundle /home/ubuntu/miningcore-review
wsl -d Ubuntu --exec git -C /home/ubuntu/miningcore-review checkout --detach <commit-sha>
wsl -d Ubuntu --exec bash /home/ubuntu/miningcore-review/scripts/test-lab/run-tests.sh
```

Create `build/test-lab` first if needed. Substitute the actual source path, a new
Linux destination, and the reviewed commit SHA. The checkout must contain only
that revision; do not overlay unrelated working edits. The runner locates its own
repository root. Keep Windows and Linux build outputs separate.

The runner restores serially with the repository's NuGet audit policy, forces a non-incremental
managed/native build, audits warnings and runs the complete .NET suite with real
coin-daemon and PostgreSQL fixtures enabled. PostgreSQL TLS fixtures cannot run as
root. Results are in `build/test-lab/build.log` and `build/test-lab/ubuntu-test-lab.trx`.
Pass ordinary `dotnet test` options after the script path when focused checks are needed.

By default, `with-postgres.sh` creates a disposable PostgreSQL cluster on a random
loopback port and removes it after the command finishes. Its randomly authenticated test-only `miningcore`
administrator is required by role-management and accounting-migration fixtures;
the shared lab database remains unchanged. To use an existing **disposable test**
database instead, set `MININGCORE_TEST_POSTGRES` to an administrator connection.
Never use a production database. For a test-only rerun of an unchanged build, the
wrapper can execute `dotnet test ... --no-build --no-restore` with the same daemon
environment variables exported by `run-tests.sh`.

The forced rebuild prevents `PreserveNewest` from retaining stale `coins.json`, schema
or example configuration after syncing older source timestamps. Do not remove it when
reusing a persistent lab. NuGet auditing must remain enabled.

## Live payout recovery and shutdown checks

After building the current source, run:

```powershell
wsl -d Ubuntu -u root --exec bash /home/ubuntu/miningcore/scripts/test-lab/test-payout-recovery.sh
```

Substitute the separate checkout path when testing a PR. Root is used only to manage
temporary systemd units; PostgreSQL, the Python driver and the recovery CLI run as `ubuntu`.
The CLI uses a non-superuser role that owns its test database and `public`, matching the documented
installation. It must still be unable to drop the protected audit schema/table, rewrite rows, disable
triggers or insert rows directly. Every cluster/role gets a fresh random SCRAM
password kept in restricted files/environment; no trust authentication or fixed password is used. The script
creates an isolated PostgreSQL cluster and temporary units, tests the real CLI in
a pseudo-terminal, and checks redirected input, default keep, cancellation, exact
release, timestamp initialization, foreground startup, planned maintenance and simultaneous
application/database shutdown, idle timeout renewal, rejected flag combinations and protected audit permissions. It asserts the CLI effective UID and directly verifies that exit
78 does not restart while an ordinary failure does. It never
reboots the host or stops the shared lab database, and removes its temporary resources.

The service fixture uses the same `Wants`/`After` ordering as the packaged helper.
Ordering applies when both jobs are scheduled: independent database maintenance still
requires stopping Miningcore first, as the [ordering guide](../../docs/systemd-postgresql-ordering.md)
documents. Run the helper's regression checks with
`bash scripts/release/test-systemd-postgresql-ordering.sh` as well.
The .NET recovery integration tests cover active locks, changed owners/tables,
concurrent recovery, a terminated guard session, missing/denied schema, bounded large-table
history, cross-schema evidence, changed database/role/endpoint, audit rollback and uncertain commits.
