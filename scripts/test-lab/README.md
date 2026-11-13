# Persistent Ubuntu test lab

The documented local lab is WSL2 `Ubuntu` (Ubuntu 26.04.1), running as the ordinary
`ubuntu` user. Keep Linux source and build outputs separate from Windows outputs.
The system installation has .NET SDK 10.0.114/runtime 10.0.14, which is below the current
runtime policy. Before running this revision, select a verified private SDK 10.0.404 or
later with both .NET and ASP.NET Core 10.0.15 or later in .NET 10, following
[the private SDK procedure](../../docs/dependency-security.md). Set
`MININGCORE_PRIVATE_DOTNET_ROOT` to that installation and put it first in `PATH` for
both build and live-test commands; retain the same `PATH` when invoking the root helper.
The helper checks the real CLI's runtime policy before creating temporary resources and
uses the selected host for its systemd units too. The lab has native build prerequisites (including
Rust/Cargo), and Bitcoin Core 31.1, Litecoin Core 0.21.5.8 and Dogecoin Core 1.14.9
at `/opt/miningcore-test-lab/bin/{bitcoind,litecoind,dogecoind}`. PostgreSQL server
binaries must be installed under `/usr/lib/postgresql/*/bin` (or selected with
`MININGCORE_TEST_POSTGRES_BIN`).

## Run the suite

### Persistent Windows native toolchain

The Windows lab also has Visual Studio 2022 with the persistent MSVC v143 x64/x86
component (`Microsoft.VisualStudio.Component.VC.Tools.x86.x64`) and Windows 11 SDK
26100 installed. The 11 November 2026 installation provides MSVC 14.44.35207.
The Odocrypt build helper discovers it through `vswhere`; no per-session toolset
override or `BuildOdoCryptWindows=false` is needed. Preserve these components when
servicing Visual Studio. Use the normal Windows build to verify native compilation,
then the repository's Odocrypt tests to verify loading and hash vectors.

### Ubuntu managed and live fixtures

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

## Wallet compatibility matrix

The additional wallet fixtures use disposable private chains and production payout
handlers, the real wallet RPCs, a real PostgreSQL control lease, and an HTTP proxy
that can discard an accepted spending response or hold it until client cancellation.
No production wallet, password or public-chain funding is needed.

Install the pinned tools as the ordinary lab user in a private directory. The
installer verifies SHA-256 before extraction and does not run package maintainer
scripts or install wallets on the system PATH. Python 3.12+ is required for safe
archive extraction. Zano needs a separate upstream **testnet** build: its mainnet
release refuses fresh-chain spending below its built-in checkpoints.

```bash
sudo apt-get install -y g++-12 cmake git curl python3 libssl-dev libzstd-dev libjitterentropy3-dev
wallet_root="$HOME/miningcore-test-lab/wallet-compatibility"
python3 scripts/test-lab/install-wallet-compatibility.py "$wallet_root"
bash scripts/test-lab/build-zano-testnet.sh "$wallet_root"
python3 scripts/test-lab/with-wallets.py --root "$wallet_root" -- \
  bash scripts/test-lab/with-postgres.sh dotnet test \
  src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~LiveWalletCompatibility' \
  --logger 'trx;LogFileName=live-wallets.trx' --results-directory build/test-lab
```

These package names are for the documented Ubuntu 26.04 lab. Build Miningcore first
with the verified SDK and export the ordinary lab daemon variables, including
`MININGCORE_TEST_BITCOIND`, as described above. To include the matrix in the full
suite, omit the filter. The wrapper sets `MININGCORE_TEST_WALLET_MANIFEST`, waits
for funding, runs the command, then stops only the process groups it created.
Without that opt-in, the wallet theory is explicitly skipped. Evidence, wallet
files and logs remain under a mode-0700 run directory; do not publish its manifest
or wallet logs, which may contain disposable wallet secrets.

| Family | Pinned wallet | Private-chain setup |
| --- | --- | --- |
| CryptoNote | Monero 0.18.5.4 | Regression chain, fixed difficulty, offline daemon; only the lab `fakechain` network label is normalized for the handler |
| Conceal | 6.7.6 | Testnet, loopback-exclusive peers, 40 normally validated difficulty-one blocks with target-spaced timestamps; default anonymity remains five |
| Zano | 2.2.3.608, source `09c052615e8327aca7d8c849f80d97f3342cc3d4` | Upstream `TESTNET=ON`, GCC 12 and checksum-pinned Boost 1.83; only gold-to-BFD linker selection changes; 30 validated private blocks |
| Equihash | Zcash 6.20.0 | Sapling regtest, no peers, legacy address RPCs explicitly enabled; blocks continue to mature asynchronous shielding/payments |
| Xelis | 1.25.0 (`b149b57a`) | Devnet's explicit PoW-skip funding mode, loopback-exclusive peers, authenticated wallet RPC |

Zcash is a legacy compatibility fixture; it does not establish compatibility with
Zebra/Zallet or recommend deploying the retired wallet. See the upstream
[zcashd retirement notice](https://github.com/zcash/zcash/blob/master/doc/book/src/user/end-of-life.md).
Sapling parameters are bundled by this version; no Sprout parameter download is
needed. These Debug lab tests retain the project's Debug peer-guard behavior for
isolated networks. They do not claim production network synchronization or live
TCP miner coverage.

Each family verifies paused admission, a successful real payment and exact
accounting, then accepted submission followed by response loss and cancellation.
Uncertainty must preserve the unpaid liability and outstanding admission, retain
ownership, and refuse resume. Xelis additionally goes offline through its actual
wallet RPC, reactivates through Miningcore, and defers spending for that cycle.
The stale-height case deliberately injects lagging scan metadata into the real
wallet's response; the regular height read must subsequently permit payment.

## PPS capacity and relay saturation

The requested capacity target is 1,000 shares/second for 60 seconds, followed by
a 10,000-share burst. `PpsCapacity_Sustains1000PerSecondAndRecovers10000ShareBurst`
and `PpsRelayCapacity_Sustains1000PerSecondAndRecovers10000ShareBurst` exercise
the production Stratum acknowledgement boundary and PostgreSQL recorder, including
real CURVE publication/receiver deduplication in the second case. They record
acknowledgement p95/p99/max, queue high-water marks, managed CPU, commit counts,
WAL bytes and PostgreSQL I/O timing. Every acknowledged share must have exactly
one statistical row and credit and the exact aggregate liability, with no overflow,
journal fallback or fail-stop. The offered-rate and recovery deadlines are asserted.

For process CPU and device latency, stop unrelated lab work first, install
`sysstat`, retain the SDK/daemon environment, and run:

```bash
bash scripts/test-lab/measure-pps-capacity.sh
```

The private results directory contains the commit, CPU/kernel/filesystem identity,
TRX, PostgreSQL `pidstat` samples and `iostat` device samples. Device measurements
describe the WSL virtual disk and may include other host activity; they are not a
production storage SLA. Repeat on the intended deployment hardware before sizing
it beyond the measured workload.

`PpsRelayOverload_BoundsMemoryAndPreservesAcknowledgedLiabilities` independently
holds transport processing, saturates an eight-message test queue with no receiver,
and proves bounded rejection/fail-stop plus recovery without losing or duplicating
acknowledged PPS liabilities. `PpsHealthySlowDatabase_KeepsEveryResponsePendingUntilCommit`
holds a real database lock for 300 ms and checks that all 100 responses remain
pending until commit. The normal production recorder capacity remains 65,536;
small capacities in saturation fixtures intentionally reach failure boundaries.

## Optional TCP Stratum payout-pause staging smoke

Run this manually before adopting a release on an isolated LTC/DOGE staging deployment.
Use disposable regtest wallets/datadirs and a disposable database, unique pool IDs and
test beneficiary addresses. Never reuse production wallets, credentials or ownership rows.
Bind Stratum and daemon RPC to loopback or an isolated lab network. Use a real Stratum
client capable of submitting valid Scrypt work; an RPC call directly to a job manager
does not satisfy this smoke test. Record the release commit, client version, config hashes,
pool IDs and test interval in a private incident record, without wallet credentials.

1. **Before pause:** start the validated unpaused LTC/DOGE pair, connect the client through
   TCP Stratum and record successful subscription/authorization, job notifications and
   accepted shares. Mine regtest candidates and confirm parent/auxiliary records and
   beneficiary attribution in PostgreSQL. Capture wallet transaction IDs and accounting
   totals as the starting point. Keep payout schemes unchanged for the entire run.
2. **Enter pause:** stop every payout-capable process in the isolated deployment, let
   in-flight work finish and retain any durable ownership marker. Set only cluster
   `paymentProcessing.paused=true`, keeping enabled accounting switches and the same pool
   IDs. Follow the [pre-start and recovery procedure](../../docs/database.md#guided-payout-ownership-recovery)
   and verify each selected binary/configuration before restarting. Reconnect the client;
   pause is restart-only, so a reconnect is expected rather than uninterrupted TCP service.
3. **During pause:** submit accepted shares and mine parent/auxiliary candidates through
   the TCP client. Confirm durable share/candidate records and the expected scheme-specific
   accounting (including liabilities for a separately provisioned PPS/PROP run). Check the
   pause warning/gauge, preserve ownership and verify there are no new pool-originated wallet
   payout sends over at least two configured payout intervals. Regtest coinbase maturation
   and unrelated wallet transactions are not evidence of a payout; compare transaction IDs
   and recipients, not only wallet balances. Fail if shares are acknowledged without the
   required durable accounting or if any payout is sent while this process is paused.
4. **Resume:** stop the client and service, reconcile candidates and uncertain wallet outcomes,
   and use the explicit guarded ownership recovery only when the runbook permits it. Remove
   the temporary pause-only service override, clear the cluster pause and restart on the
   same history. Reconnect the TCP client and confirm accepted shares and new jobs again.
   Mature the staged backlog and observe at least two actual payout-manager cycles. Verify
   recorded wallet transaction IDs, balance/reward conservation and no duplicate allocation
   or sends for the same obligations.
5. **Evidence and cleanup:** retain redacted client protocol logs, timestamps, database
   counts/accounting totals, wallet transaction IDs and the pre-start receipts for all three
   phases. Mark the smoke failed or incomplete if any required observation is missing; merely
   connecting or receiving a job is insufficient. Stop the isolated processes and remove only
   their disposable resources after evidence capture.

This is an opt-in manual acceptance procedure, not a claim that TCP staging ran in CI.
The automated `PausedExample_RealStartupMiningPersistenceAndOwnershipRecovery` fixture
exercises production job managers directly. The additional #291 scenarios are covered by
the fixtures below; neither fixture group claims this optional TCP mining smoke ran.

## Combined pause, relay, backlog and PPS outage scenarios

Run `WalletPayoutControlIntegrationTests` through the runner above, or use its same daemon
environment and `with-postgres.sh` wrapper with a previously built, unchanged revision:

```bash
bash scripts/test-lab/with-postgres.sh dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj \
  --no-build --no-restore --filter 'FullyQualifiedName~WalletPayoutControlIntegrationTests' \
  --logger 'trx;LogFileName=wallet-topologies.trx' --results-directory build/test-lab
```

Allow at least 20 minutes for the focused group. Every database, role, wallet, daemon and
child service belongs to the fixture; the outage case starts and abruptly stops its own
loopback PostgreSQL server, never the shared lab server. Runtime roles remain distinct
from table owners and wallet-control operators. The private outage server uses the existing
TLS-policy fixture's loopback-only trust authentication; the outer lab wrapper uses SCRAM.

- `MixedLocalPauseProcesses_ShareWalletAndDatabaseWithoutSharingOwnership` starts separate
  locally paused and active service processes on one database/wallet. A third payout owner
  must fail. It distinguishes the local flag from the database stop, restarts the owner,
  and matches database payment receipts to actual wallet sends.
- `ExtendedPausedRelay_PersistsPpsAndMonitorsBoundedQueuesAndBacklog` runs for at least
  **620 real seconds**, crossing two production five-minute pause heartbeats. It feeds
  50 immutable PPS shares approximately every five seconds through real ZeroMQ and the
  production receiver/recorder, checks each durable credit and balance, keeps 64 blocks
  pending, and records queue high-water, overflow, storage growth and zero wallet sends.
  Synthetic validated evidence isolates relay/accounting behavior; it is not proof of TCP
  mining. Test-only repeats of the same IDs compensate for PUB/SUB loss and verify deduplication.
- `LargePausedBacklog_RestartsBetweenMaturityWavesWithoutDuplicateAllocationOrPayment`
  records 256 real regtest block/coinbase identities while locally paused, then uses three
  payout-process lifetimes and two maturity waves. It checks complete classification,
  reward/balance conservation, exactly two payment receipts and matching wallet transactions.
- `PausedPps_DatabaseInterruptionRequiresDurabilityBeforeAcknowledgementAndExplicitRecovery`
  holds a real database statement pending, abruptly stops PostgreSQL, and checks that PPS
  responses wait for authenticated journal durability. A fresh recorder imports and replays
  the evidence exactly once. A second outage plus injected journal-flush failure must produce
  no positive responses and must retain a fatal incident that still blocks startup after
  PostgreSQL returns. It invokes the production Stratum publish/acknowledgement boundary
  directly, without a TCP mining client.
- `PpsRelaySender_CommitsLocallyAndReceiverDeduplicates` verifies local PPS durability with
  the receiver absent, followed by real relay delivery and receipt deduplication in the same
  database. Mixed-scheme statistical traffic must not be duplicated by the sender's recorder.

The soak's bounded measurements describe that traffic and duration, not an indefinite pause
or capacity benchmark. Local pause suspends payout reconciliation/retention while PPS liabilities
continue growing. Monitor disk capacity, queue pressure, pending blocks, recovery evidence and
wallet reserves; stop admission before capacity is exhausted. Keep the shared database and
coordinated-version boundaries in the [wallet-stop guide](../../docs/emergency-wallet-stop.md).

## Live payout recovery and shutdown checks

For database emergency wallet admission, run the full suite and the focused
`FullyQualifiedName~WalletPayoutControlIntegrationTests` cases. They create disposable
databases and authenticated runtime/operator roles, start competing process hosts,
restart a private PostgreSQL server and exercise a real Bitcoin wallet. See the
[protocol, rollout policy and verified results](../../docs/emergency-wallet-stop.md).

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

The recovery live check also performs a logical dump/restore into another disposable database,
verifies that changed ownership OIDs refuse release before migration, reapplies the audit migration,
and confirms a guarded release preserves the original receipts. It also verifies the pool role
can still read old history but cannot append through retired endpoints, even with the payout lock. It covers both timestamp-detection
failures returning 78 from Main. The CI integration suite launches fresh process hosts for explicit
timestamp modes before Npgsql initialization and both detection failures; the terminal lab also
covers the normal-startup prompt path.

The live check also executes the [dedicated-login GRANT example and acceptance SQL](../../docs/database.md#dedicated-recovery-identity-and-permissions)
directly from the guide. It rejects missing permissions, PUBLIC/inherited application execution,
non-inherited recovery-role membership and migration-restored defaults, then proves with actual
database logins that application release is denied while restricted recovery records its own identity.
It also rejects audit DML/column-write grants, schema CREATE, administrative attributes, role
memberships and append-function ownership. A real terminal check verifies that a restricted login
keeps protection and receives dedicated-identity guidance before any menu or confirmations.
The managed-PostgreSQL migration check uses a real non-superuser administrator login,
proves the index creation fails without inherited owner privileges, applies the documented
grant direction, and verifies that the application cannot drop administrator-owned audit storage.
It reruns the migration twice after revoking both temporary grants, verifies the index identity
is preserved, and rejects malformed or missing indexes without changing audit history.
Foreground recovery also runs with console logging disabled and restrictive service thresholds:
the audit receipt remains visible during recovery and the service console settings are restored
before normal startup continues.

The automated restore drill uses two databases within one disposable cluster. Before production
RC.2 deployment, also complete the [cross-cluster acceptance rehearsal](../../docs/database.md#cross-cluster-release-acceptance)
between separately administered staging clusters. Record destination role/ACL, authentication and
service checks there; a passing local drill does not establish those deployment-specific results.

Before promoting the broader RC.2 release, enable the pinned Bitcoin BLAKE2b Knots regtest binary
with `MININGCORE_TEST_BLAKE2B_BITCOIND` in addition to the three standard daemon variables. Use the
checksum/version from `.github/workflows/dotnet.yml`, keep it in the isolated lab, and run the full
suite through `with-postgres.sh`. This includes uncertain wallet submissions, mempool loss,
restart/revalidation and duplicate-payment checks. Record remaining optional daemon/hardware
skips individually; a passing PostgreSQL recovery test does not replace wallet reconciliation.
