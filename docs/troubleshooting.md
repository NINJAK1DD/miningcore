# Troubleshooting Miningcore

Use this guide to identify the failing boundary and reach the authoritative procedure quickly. It
is not a substitute for the database, wallet or recovery runbooks linked below.

## Safety rules

Before changing anything:

- Preserve the service journal, Miningcore logs, configuration, recovery journal and recovery-state
  directory.
- Redact RPC passwords, database passwords, administrative tokens, licence keys, wallet addresses,
  complete transaction IDs, peer addresses and private network details before sharing evidence.
- Prefer read-only API, wallet and SQL checks. Do not edit balances, blocks, payments, payout
  ownership or recovery files unless a documented procedure explicitly requires it.
- Stop Miningcore before stopping PostgreSQL or payout wallets during planned maintenance.
- Treat an uncertain wallet submission as potentially paid until the daemon and database prove
  otherwise.

For a production accounting incident, make a copy of the relevant evidence before restarting or
rotating logs.

## Quick triage

Run these checks on a systemd deployment. Substitute configured ports and service names:

```console
sudo systemctl status miningcore --no-pager -l
sudo journalctl -u miningcore --since '30 minutes ago' --no-pager
sudo ss -ltnp
curl --fail --max-time 5 http://127.0.0.1:4000/api/health-check
curl --fail --max-time 5 http://127.0.0.1:4000/api/pools
curl --fail --max-time 5 http://127.0.0.1:4002/metrics --output /dev/null
df -h
df -i
```

These commands answer five initial questions:

1. Did the process start, exit, or repeatedly restart?
2. Did configuration validation fail before listeners or pools were created?
3. Are the expected public, admin, metrics and Stratum ports listening?
4. Is the API responding and are the intended pools online?
5. Do the database, logs, recovery state and wallets still have usable storage?

An active process alone does not prove mining or payouts are healthy.

## When an RC.1 upgrade stops

Start here if the [RC.1 archive upgrade block](releases.md#upgrade-to-v040-rc1)
prints `STOP`. A stop is a request to investigate, not an instruction to disable
a check. Most automated tests run in CI; operators do not need to run the test
suite to follow this procedure.

### First, read the state lines

The command prints `Failed step`, `Service` and `Schema`, followed by a reference
to this section. Keep the first underlying error as well as these lines. They
describe **what this command attempted**, not everything that may have happened
on other nodes or in the database. Here, **schema** means the database structure
(tables, indexes and functions), not the balances or other records stored in it.

- **`no stop requested by this block`:** the command did not stop the local
  service. It does not claim the service is running; you may already have stopped
  it manually. No restart is required just because this check failed.
- **`stop requested; completion unconfirmed`:** check the actual service status.
  Do not assume a failed stop means the process kept running or safely exited.
- **`stop confirmed by systemctl`:** the local service stopped successfully.
  Other services, relays and remote writers still require separate checks.
- **`no schema-changing SQL attempted by this block`:** this command has not run
  a migration. It does not mean no application, wallet or external writer changed
  any data. Ownership preflights and backup checks do not modify the schema.
- **`migration SQL attempted; changes may have committed`:** keep Miningcore
  stopped and ask the database administrator to establish which scripts committed.
  Do not rerun migrations, restore an old backup or start the old binary blindly.

The block does not restart the service automatically, including after `READY`.
Complete the [startup and cutover checks](upgrade-v0.4.0-rc.1.md#staging-and-cutover)
before reconnecting miners. This section does not replace financial recovery or
the [rollback boundary](upgrade-v0.4.0-rc.1.md#rollback-and-release-evidence).

### Find the failed step

| Failed step shown | What to check next |
| --- | --- |
| `archive-readiness` | Complete the [download and verification](releases.md#choose-a-version) in this shell. A download alone is not verification. Do not set the readiness variable manually. |
| `release-version` | This block accepts only RC.1. Use the [stable v0.3.0 route](releases.md#upgrade-to-published-stable-v030) for that release. Select RC.1 explicitly only when its verified assets are available. |
| `migration-plan` | Choose the plan from the [migration matrix](upgrade-v0.4.0-rc.1.md#database-migration-matrix). Do not guess from the existence of a table. Each retry needs a fresh selection. |
| `release-source` | Set `MININGCORE_EXPECTED_SOURCE_COMMIT` from independently verified release provenance or the verified release tag. It is the full lowercase source commit identifier, not the archive checksum, a branch name or a short hash. If unsure, ask the release operator. Never copy it from the candidate itself just to make it pass. |
| `release-target` | Select the verified 22.04 or 26.04 archive matching this host. Other hosts require their documented source-build route, not a relabelled archive. |
| `candidate-directory` | An immutable candidate directory already exists. Inspect its version, source identity and whether extraction was partial. Do not extract over it or delete the active installation. Ask an administrator if its ownership or purpose is unclear. |
| `staging-directory` or `archive-extraction` | Read the preceding filesystem/archive error. Check available space, the verified archive and administrator access to the intended installation directory. Do not broaden permissions or use a different host's archive. |
| `migration-files` | The extracted candidate lacks its expected `migrations/` directory. Verify the archive, versioned directory and host target; do not copy SQL from a moving branch. |
| `share-owner-file` or `arithmetic-owner-file` | `check-share-owner.sql` or `check-pps-arithmetic-ownership.sql` is missing or unreadable under `scripts/release/`. Recheck the complete verified RC.1 package. A stable v0.3.0 archive intentionally lacks these files; use its own runbook. Never skip the check. |
| `selected-migration-files` | A SQL file required by the chosen plan is absent or unreadable. `pps-arithmetic` requires `add_pps_arithmetic_version.sql`; `cumulative` requires `add_auxpow_block_idempotency.sql`, `add_payout_manager_ownership.sql` and `add_share_accounting.sql`. Verify the complete candidate and access permissions; do not skip a migration or fetch it from a moving branch. `none` requires no migration SQL. |
| `build-info` or `candidate-version` | Verify the complete candidate, BUILD-INFO, .NET runtime, host release and native dependencies. Keep the first launch/read error; do not replace only a managed DLL. |
| `candidate-identity` | BUILD-INFO does not match the selected version, Ubuntu target or independently verified source SHA. Stop and reconcile the release identity; never edit the metadata or expected SHA to match an unverified archive. A runtime version/SHA mismatch is reported as `candidate-version`. Both occur before shutdown. |
| `backup-location` | A new private backup directory could not be created. Check free space and access to the backup destination. The service has not been stopped by this block. Do not reuse or truncate an old backup to get past the check. |
| `service-stop` | Use the read-only service checks below and confirm all processes using this database. Ask the host administrator to resolve service permissions or a stuck shutdown before continuing. |
| `database-backup` | Check the preceding backup/redirection error, disk space and the documented PostgreSQL backup identity. Each attempt prints a new private destination before dumping. Existing backups are never overwritten, and a pre-existing file at the new destination is rejected. Keep that directory and any partial file private; do not treat the file as a usable backup or proceed without a verified backup. |
| `backup-inventory` | The archive inventory could not be read. Do not migrate. Preserve the error and have the database administrator establish a usable, restorable backup. An inventory check is not a full restore rehearsal. |
| `share-owner-preflight` or `arithmetic-owner-preflight` | Follow the [ownership precondition](pps-arithmetic-migration.md#standalone-ownership-precondition). Missing tables, superuser owners and mismatched owners need diagnosis, not automatic grants or fallback. A database administrator must review ownership repairs. |
| `auxpow-migration`, `payout-ownership-migration`, `accounting-migration` or `arithmetic-migration` | Preserve the exact first SQL error privately. Earlier migrations may have committed. Keep writers stopped and use the [database runbook](database.md#upgrade-an-existing-database) with a database administrator; do not rerun the whole sequence or restore over newer wallet/payment evidence. |
| `activation` | Candidate activation was not confirmed. Inspect the actual `/opt/miningcore` link and recorded release identity. Even if the old link remains, do not restart an old binary against a potentially changed database. Follow the rollback boundary with the administrator. |

Read-only local checks (substitute your real service name; these commands do not
start, stop, repair or migrate anything):

```console
sudo systemctl status miningcore --no-pager -l
sudo journalctl -u miningcore --since '30 minutes ago' --no-pager
readlink -f /opt/miningcore
df -h
df -i
```

For containers or source installations, use the corresponding
[deployment procedure](upgrade-v0.4.0-rc.1.md); do not paste archive/systemd
activation commands into a different layout. Container extraction failures retain
the printed output directory, which may be incomplete. A cleanup error names the
temporary container; never remove a live pool container or production volume to
clear it.

### Startup fails after activation

Use the **first** startup error, not a later connection failure. For common cases:

- SMTP errors naming `notifications.email` need the
  [SMTP configuration checks](upgrade-v0.4.0-rc.1.md#notifications-and-smtp).
  Port 587 with authentication needs `startTls` and both credentials; never post
  the actual credentials or disable certificate validation to make it start.
- A missing/malformed share-accounting schema message may name
  `add_share_accounting.sql` generically. An otherwise complete v0.3.0 accounting
  schema may need only the standalone arithmetic migration, or a privilege/default
  correction. Use the [RC.1 matrix](upgrade-v0.4.0-rc.1.md#database-migration-matrix)
  before applying SQL. Startup refusal is not permission to create lookalike tables.
- TLS, PostgreSQL timeout and VarDiff errors need the named setting checked against
  the [configuration guide](upgrade-v0.4.0-rc.1.md#configuration-checks), not a copied
  configuration that replaces your working pool identities or payout choices.

For a **new** database import, an error such as `relation already exists` is not
an instruction to delete that relation or rerun `createdb.sql`. Confirm the target
database and whether it is truly empty with the database administrator. See
[new database versus upgrade](database.md#new-installation).

### When to ask for help

Ask the host/database administrator if service state is uncertain, a backup cannot
be proved, ownership/privileges need changing, migration SQL was attempted, or
activation/financial state is unclear. Do not repair balances, delete receipts,
clear payout ownership or discard recovery journals just to get a green startup.

For support, provide the release/commit, OS, installation method, payout modes,
fixed failed-step name and relevant state lines, plus the first **redacted** error.
Do not post whole configs, DSNs, passwords, tokens, wallet contents or database
dumps. Review command output for private paths, endpoints and miner/transaction
identifiers before sharing; retain the original privately.

## Symptom index

- **Configuration error at startup.** Read the first error and validate the named path without
  weakening security controls. Continue with [configuration validation](configuration.md#validate-changes-safely).
- **`AddressAlreadyInUse` or listener retry.** Identify the named endpoint and owning process. Do
  not add `SO_REUSEADDR` or shorten the safety boundary. Continue with
  [Stratum listener reservation](configuration.md#stratum-listener-reservation).
- **Pool remains offline.** Check daemon reachability, synchronization, wallet availability and the
  first pool-specific error. Continue with [pool and daemon checks](#pool-and-daemon-checks).
- **Miner connects but submits no accepted shares.** Confirm the port, coin, address format,
  password options and difficulty. Continue with [miner and share checks](#miner-and-share-checks).
- **A direct-SOLO miner receives no job or reports zero coinbase share.** Confirm direct settlement
  is active on the canonical BTC SOLO pool (the `v0.3.0` default unless explicitly disabled), the
  base username is a valid address for the daemon network, and the fee placeholder was replaced.
  Decode the announced coinbase before hashing; do not fall back to the pool wallet. Follow the
  [Bitcoin direct-SOLO preflight](bitcoin-direct-solo.md#preflight-before-production-hashing).
- **BIP310 version rolling is declined or a custom mask stops startup.** Do not widen the mask to
  satisfy a miner. Check the per-chain audit and diagnostics in
  [Bitcoin-family version rolling](version-rolling.md).
- **A Bitcoin-family proxy gets subscribe error `20` or disconnects after subscribing again.**
  Subscribe once on each upstream TCP connection. The first duplicate preserves outstanding
  work; another closes the connection. Check `miningcore_stratum_admission_total` outcomes
  `duplicate-subscribe-warning` and `duplicate-subscribe` to distinguish single retries from
  retry loops. Test affected firmware on an isolated endpoint and reconnect for a fresh
  assignment. See [subscription compatibility](bitcoin-subscription-policy.md).
- **A miner shows nearly 100% rejected shares immediately after BIP310 configuration.** Check the
  pool log for a declined version-rolling negotiation and update incompatible miner firmware. Do
  not widen the template mask; consensus-owned version bits must remain daemon-controlled.
- **Public API works but admin or metrics returns `404`.** Use the configured dedicated port;
  wrong-listener routes deliberately return `404`. See the [API listener matrix](api.md#configuration).
- **Admin returns `401`, `403`, `429` or `503`.** Check authentication, source whitelist, rate
  limiting and credential availability. See [administrative API security](admin-api-security.md).
- **Metrics returns `403` or `405`.** Verify the observed source address and use exact uppercase
  `GET` or `HEAD`. See [metrics and administration](api.md#metrics-and-administration).
- **Storage exhaustion stops PostgreSQL or Miningcore.** Preserve accounting evidence and restore
  writable space without deleting journals. Follow [disk-exhaustion recovery](database.md#recover-after-disk-exhaustion).
- **Exit status `74` or a fatal recovery latch.** Keep miners offline and reconcile every incident
  before acknowledgement. Follow [fatal-state recovery](database.md#reconcile-fatal-share-recovery-state).
- **`Unidentified shares must not carry partial accounting data` on v0.2.0 `SOLO`/`SOLO`
  merged mining.** Stop the restart loop and preserve the normal recovery journal, every quarantine
  file, the recovery-state directory and PostgreSQL state. Upgrade to the
  [v0.2.1 hotfix](releases.md#v021-hotfix) or later before resuming merged mining. Import only a
  verified normal recovery journal through `-rs`; quarantine files require manual financial
  reconciliation and must never be imported.
- **The recovery journal contains records.** Preserve the source and use the manifested one-shot
  importer. Follow [recovery-journal import](database.md#inspect-and-import-a-recovery-journal).
- **Another payout manager owns the database.** Prove the previous process and PostgreSQL backend
  are dead, then inspect uncertain payments. Follow [ownership recovery](database.md#recover-payout-manager-ownership-safely).
- **Wallet reports `Insufficient funds` code `-6`.** Check confirmed inputs, fee ownership and
  fallback mode; do not manufacture a payment. See [fee reserve readiness](operations.md#fee-reserve-and-balance-readiness).
- **Wallet response or payout identity is uncertain.** Assume the request may have succeeded and
  reconcile the daemon transaction before changing ownership. Follow
  [Bitcoin-family payout reconciliation](database.md#reconcile-a-bitcoin-family-payout).
- **`Auxiliary template update failed`.** Confirm whether recovery followed; inspect DOGE sync, RPC
  pressure and auxiliary metrics before changing the timeout. See [template refresh](merged-mining-litecoin-dogecoin.md#template-refresh).
- **Startup says the share-accounting schema is missing or malformed.** Keep listeners offline
  and verify the intended database/search path and privileges. The runtime message names the
  cumulative migration generically; use the [release-specific matrix](upgrade-v0.4.0-rc.1.md#database-migration-matrix)
  before choosing SQL. A complete v0.3.0 accounting schema may need only the standalone arithmetic
  migration. A missing pruning singleton/index can require a different repair with significant
  maintenance time. Do not create lookalike tables or disable preflight. See
  [startup recovery guidance](#startup-fails-after-activation) and the
  [database procedure](database.md#upgrade-an-existing-database).
- **Startup says the direct-settlement block schema is missing or malformed.** Keep Bitcoin direct
  SOLO offline and apply `add_bitcoin_direct_solo.sql` from the verified candidate directory while
  every database writer is stopped. Do not create same-named columns or constraints manually; the
  exact types, settlement/submission-state constraint, reconciliation order and prepared-submission
  replay index are checked before work is delivered. See the
  [direct-SOLO database migration](bitcoin-direct-solo.md#database-migration).
- **Startup reports replaying Bitcoin direct-SOLO outbox entries.** Keep Stratum closed until replay
  finishes. Preserve PostgreSQL and the recovery journal, then confirm each exact block becomes
  `observed-active`, remains `submitted-uncertain`, or reaches the documented bounded rejection
  threshold. Do not delete a prepared row or manually submit reconstructed data; the stored exact
  payload is authoritative. See
  [direct-SOLO confirmation and restart behavior](bitcoin-direct-solo.md#confirmation-restart-and-reorg-behavior).
- **Miningcore stops after a direct block with a PostgreSQL commit or cleanup warning.** Keep the
  exact recovery journal and PostgreSQL row. Miningcore attempts daemon propagation before the
  database-health fail-stop; an uncertain commit deliberately writes an importable, idempotent
  journal entry even when PostgreSQL may also contain the same outbox row. Verify and reconcile the
  stable candidate identity before recovery import or restart. Do not convert that journal into a
  quarantine file merely because both copies exist. See
  [direct-SOLO confirmation and restart behavior](bitcoin-direct-solo.md#confirmation-restart-and-reorg-behavior).
- **A Bitcoin direct-SOLO template update stops Miningcore.** Keep the supervised restart loop
  stopped if the error repeats. The pool deliberately invalidates stale direct jobs and closes
  mining admission when a post-startup template cannot satisfy the immutable coinbase contract.
  Preserve PostgreSQL and every recovery/quarantine artifact, inspect the daemon template and use
  the [direct-SOLO confirmation and rollback boundaries](bitcoin-direct-solo.md#confirmation-restart-and-reorg-behavior)
  before resuming miners.
- **PPS balances grow while blocks are orphaned or absent.** This is expected PPS liability, not a
  reason to edit balances. Check the reserve, exact PPS ledger, remainder table and bounded
  liability/replay metrics. See
  [PPS economic and support boundary](pps.md#economic-and-support-boundary).
- **Merged miners are rejected for missing DOGE attribution.** Non-SOLO auxiliary pools require
  `requireAuxAddress: true` and a daemon-validated `doge=` address. Check the bounded attribution
  rejection metric; never substitute the pool wallet as a beneficiary.
- **Accounting relay format is unsupported.** Upgrade and migrate receivers first, stop old senders,
  then upgrade senders. Do not enable pooled merged mining during a mixed-version window. See
  [relay database boundary](merged-mining-litecoin-dogecoin.md#relay-database-boundary).
- **Relay receiver is unavailable.** Restore the route and receiver; ordinary PUB/SUB shares sent
  during the outage are not replayed. See [share-relay durability](share-relays.md#durability-boundary).
- **Logs consume unexpected space.** Inspect Miningcore's native archives and remove conflicting
  external `copytruncate` rules. See [log rotation](configuration.md#log-files-and-rotation).
- **Upgrade or container replacement failed.** Keep the old immutable installation and database
  backup. Follow the [upgrade and rollback boundary](releases.md#upgrade-or-roll-back).

## Startup and configuration

Run the binary interactively only during a controlled diagnostic window, using the same working
directory, service account, environment and configuration path as the service. A command that works
as an administrator may still fail under the restricted service identity.

For systemd, inspect the effective launch contract:

```console
sudo systemctl show miningcore \
  -p User -p Group -p WorkingDirectory -p ExecStart \
  -p EnvironmentFiles -p StateDirectory --no-pager
sudo systemctl cat miningcore
```

Common causes include:

- an unresolved `CHANGE_ME` placeholder;
- a missing per-pool `paymentProcessing` object;
- a null Stratum endpoint;
- a listener address that is not bindable in the service or container network namespace;
- missing database migrations;
- unreadable configuration, certificate, coin-template or state files; and
- a relative path being resolved from a different systemd working directory.

Do not make the service root merely to bypass a permissions error. Correct ownership and grant only
the required path access.

## Pool and daemon checks

Check the public pool response and the matching pool log. Then query the daemon with the exact data
directory, network, RPC authentication, wallet and proxy arguments used by Miningcore:

```console
curl --fail --max-time 5 http://127.0.0.1:4000/api/pools
sudo journalctl -u miningcore --since '15 minutes ago' --no-pager
```

For each affected daemon, verify:

- the process is running and its RPC port is reachable only from intended clients;
- blockchain synchronization is complete enough for template creation;
- the configured wallet is loaded and usable when payment processing is enabled;
- configured ZMQ endpoints exactly match the daemon and Miningcore settings; and
- the node has peers, current time and sufficient disk space.

Use daemon documentation for authoritative RPC names and sync fields. Never paste a command line
containing an RPC password into public evidence.

## Miner and share checks

Confirm the miner uses the intended pool's Stratum endpoint. For merged mining, miners connect to
the Litecoin parent endpoint, put the Litecoin address in the username and provide the Dogecoin
beneficiary through the configured password key.

Check, in order:

1. DNS and TCP reachability from the miner network.
2. TLS mode and any trusted PROXY-protocol hop.
3. Username, worker suffix and coin-address validity.
4. Password parameters such as `d=` and `doge=`.
5. Initial difficulty and VarDiff suitability for the device hashrate.
6. Accepted/rejected share counters, recent database shares and pool-specific logs.

One miner submitting shares proves the endpoint works for that route; it does not validate another
network, TLS proxy or firewall path.

For Bitcoin direct SOLO, no `mining.notify` is sent before successful authorization. Repeated
authorization with another address clears that connection's old jobs and creates a new immutable
projection. An old or another connection's job ID must fail instead of redirecting its payout.

## API, administration and metrics

The public, administrative and metrics route families may use three different local ports. A `404`
on the wrong listener is intentional route isolation, not evidence that the route was removed.

Status interpretation:

- `401`: bearer token is absent or invalid.
- `403`: the source address failed the route's IP whitelist.
- `404`: the route is on another listener, or it does not exist.
- `405`: metrics received a method other than exact `GET` or `HEAD`.
- `429`: the source hit the API rate limiter.
- `503`: the administrative credential was absent or invalid at process startup.

Do not place the administrative token in a URL, JSON configuration, public dashboard or ordinary
shell command argument. Use the stdin-header pattern in the
[administrative API security guide](admin-api-security.md#call-the-api).

## Payout and accounting incidents

Preserve these identities privately before taking action:

- pool ID and affected balance address;
- block height, type, status and abbreviated hash;
- payout-manager ownership generation and process identity;
- payment-batch identity and abbreviated wallet transaction ID; and
- the service-journal time range covering submission and persistence.

Compare the wallet, `blocks`, `balances`, `payments`, `payment_batches`,
`payout_manager_ownership`, `share_accounting_groups`, `share_accounting_prune_state`,
`pps_share_credits` and `pps_credit_remainders` records through the documented reconciliation
procedure. Never infer
failure only from a missing HTTP response or email notification. Do not delete an accounting group
to retry it: its UUID and payload hash are the exactly-once evidence.

## Collecting shareable evidence

Prefer a small, bounded evidence set:

```console
sudo systemctl status miningcore --no-pager -l
sudo journalctl -u miningcore --since '30 minutes ago' --no-pager
sudo ss -ltnp
df -h
df -i
```

Also record the release tag or commit, deployment type, operating-system release, whether the error
survives a controlled restart, and the exact first error. Share only the relevant time range.

Before publishing output, redact:

- environment-variable values and configuration secrets;
- wallet and miner addresses;
- complete transaction and block identifiers;
- public and private peer addresses;
- hostnames, usernames, process command lines containing credentials and internal topology; and
- database rows not required to demonstrate the problem.

Keep an unredacted copy in protected incident storage so the financial audit trail remains usable.

## Further reading

- [Operator handbook](operations.md)
- [Configuration guide](configuration.md)
- [Database and recovery guide](database.md)
- [Release installation and rollback](releases.md)
- [API and monitoring](api.md)
- [Pay Per Share operation](pps.md)
- [Litecoin–Dogecoin merged mining](merged-mining-litecoin-dogecoin.md)
- [Share relays](share-relays.md)
