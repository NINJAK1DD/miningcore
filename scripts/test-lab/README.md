# Persistent Ubuntu test lab

The documented local lab is WSL2 `Ubuntu` (Ubuntu 26.04.1), running as the ordinary
`ubuntu` user. Keep Linux source and build outputs separate from Windows outputs.
The lab has .NET SDK 10.0.114/runtime 10.0.14, native build prerequisites (including
Rust/Cargo), and Bitcoin Core 31.1, Litecoin Core 0.21.5.8 and Dogecoin Core 1.14.9
at `/opt/miningcore-test-lab/bin/{bitcoind,litecoind,dogecoind}`. PostgreSQL's isolated
accounting cluster listens on loopback port 55432 with database/user
`miningcore_tests`/`miningcore` and test-only password `miningcore-test`.

## Run the suite

For the primary Windows checkout (a normal Git directory, not a linked worktree),
run from PowerShell:

```powershell
wsl -d Ubuntu --exec bash /mnt/c/Users/Admin/Documents/Codex/miningcore/scripts/test-lab/sync-wsl.sh /mnt/c/Users/Admin/Documents/Codex/miningcore /home/ubuntu/miningcore
wsl -d Ubuntu --exec bash /home/ubuntu/miningcore/scripts/test-lab/run-tests.sh
```

Sync includes uncommitted source; it overwrites the Linux test copy. Edit the Windows
source. For a PR, validate a clean exported commit/tree in a separate Linux checkout
instead so unrelated local edits cannot affect the results. Run `scripts/test-lab/run-tests.sh`
from that checkout; the runner locates its own repository root.

The runner restores with the repository's NuGet audit policy, forces a non-incremental
managed/native build, audits warnings and runs the complete .NET suite with real
coin-daemon and PostgreSQL fixtures enabled. PostgreSQL TLS fixtures cannot run as
root. Results are in `build/test-lab/build.log` and `build/test-lab/ubuntu-test-lab.trx`.
Pass ordinary `dotnet test` options after the script path when focused checks are needed.

The forced rebuild prevents `PreserveNewest` from retaining stale `coins.json`, schema
or example configuration after syncing older source timestamps. Do not remove it when
reusing a persistent lab. NuGet auditing must remain enabled.

## Live payout recovery and shutdown checks

After building the current source, run:

```powershell
wsl -d Ubuntu -u root --exec bash /home/ubuntu/miningcore/scripts/test-lab/test-payout-recovery.sh
```

Substitute the separate checkout path when testing a PR. Root is used only to manage
temporary systemd units; PostgreSQL and the payout process run as `ubuntu`. The script
creates an isolated PostgreSQL cluster and two temporary units, tests the real CLI in
a pseudo-terminal, and checks redirected input, default keep, cancellation, exact
release, planned maintenance and simultaneous application/database shutdown. It never
reboots the host or stops the shared lab database, and removes its temporary resources.

The service fixture uses the same `Wants`/`After` ordering as the packaged helper.
Ordering applies when both jobs are scheduled: independent database maintenance still
requires stopping Miningcore first, as the [ordering guide](../../docs/systemd-postgresql-ordering.md)
documents. Run the helper's regression checks with
`bash scripts/release/test-systemd-postgresql-ordering.sh` as well.
The .NET recovery integration tests cover active locks, changed owners/tables,
concurrent recovery and a terminated guard session.
