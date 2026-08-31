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
- **BIP310 version rolling is declined or a custom mask stops startup.** Do not widen the mask to
  satisfy a miner. Check the per-chain audit and diagnostics in
  [Bitcoin-family version rolling](version-rolling.md).
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
- **Startup says the share-accounting schema is missing or malformed.** Keep listeners offline,
  verify the intended database/search path and apply `add_share_accounting.sql` during a maintenance
  window. The preflight includes the required `share_accounting_prune_state` singleton and exact
  ascending B-tree pruning index; rerunning the migration safely restores either one but rebuilds
  the index, which can take significant maintenance time on a large accounting table. Do not create
  lookalike tables or disable preflight. See
  [database upgrades](database.md#upgrade-an-existing-database).
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
