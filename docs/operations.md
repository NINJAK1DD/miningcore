# Miningcore operator handbook

This is the day-to-day checklist for a production Miningcore service. It deliberately links to the
authoritative procedures instead of duplicating recovery SQL or release commands.

## Before accepting miners

- Use a maintained Linux host and keep the .NET 10 runtime, PostgreSQL and coin daemons serviced.
- Keep wallet RPC, daemon RPC, PostgreSQL, the admin API, metrics and relay ports on trusted networks.
- Run exactly one payout manager for each database/pool set. Automatic payout-manager failover is not
  supported.
- Store the production configuration and licence environment outside the application directory with
  service-account-only permissions.
- Put `shareRecoveryFile` on separately monitored or reserved storage when possible, and persist
  `shareRecoveryStateDirectory` across service or container replacement.
- Use a service manager whose forced-stop timeout exceeds Miningcore's 45-second accounting budget.
  The supplied systemd unit uses 90 seconds.
- Verify a PostgreSQL backup and retain the previous immutable application directory or container
  before every upgrade.

Use the [release guide](releases.md) for installation, the [configuration guide](configuration.md)
for settings, and the [database guide](database.md) for backup and schema preparation.

## Normal service checks

For a systemd installation:

```console
sudo systemctl status miningcore --no-pager
sudo journalctl -u miningcore --since '30 minutes ago' --no-pager
curl --fail http://127.0.0.1:4000/api/health-check
curl --fail http://127.0.0.1:4000/api/pools
```

Confirm that:

- every enabled pool is online and its daemon is synchronized;
- expected Stratum ports are listening and accepting representative test miners;
- shares continue to reach PostgreSQL;
- the intended process owns payout processing and no uncertain payout is awaiting reconciliation;
- disk space and inodes are healthy on database, log, journal and service-state filesystems; and
- backups complete and can be inspected or restored on the planned schedule.

Do not treat an active process alone as proof of a healthy pool. Check the API, recent shares, daemon
height and the administrative logs together.

## Monitoring and alerts

Monitor at least:

- process availability, restart count and non-zero exit status;
- daemon sync, peer state and wallet/RPC errors;
- pool connections, hashrate, accepted/rejected shares and last block time;
- PostgreSQL health, transaction latency, backup age and free storage;
- wallet balances, payout ownership and uncertain payment/block events;
- Miningcore log growth and filesystem free bytes/inodes; and
- primary and emergency persistence-queue depth, high-water mark, capacity and overflow count.

Prometheus metric names and queue labels are documented in [API and monitoring](api.md#metrics-and-administration).
Administrative email or Pushover delivery is an extra signal; the service journal, durable recovery
state and database remain authoritative.

## Safe stop and start order

For planned maintenance, stop Miningcore before PostgreSQL or coin wallets so active persistence can
finish and payout ownership can be released cleanly:

```console
sudo systemctl stop miningcore
sudo systemctl is-active miningcore
pgrep -af 'Miningcore|Miningcore.dll' || true
```

Start PostgreSQL and the required daemons first, wait for them to become ready, then start Miningcore.
Read the complete startup log before returning traffic. Do not force-kill the process merely because
shutdown takes several seconds; candidate delivery and share recovery intentionally outlive ordinary
request cancellation within the bounded shutdown window.

## Incident routing

| Symptom | First action | Procedure |
| --- | --- | --- |
| Disk full, PostgreSQL or Miningcore will not start | Preserve logs and recovery files; restore writable space without deleting accounting evidence | [Disk-exhaustion recovery](database.md#recover-after-disk-exhaustion) |
| Exit status 74 or a share-recovery fatal latch | Keep miners offline, verify all incident evidence and reconcile PostgreSQL before acknowledgement | [Share-recovery fatal state](database.md#reconcile-fatal-share-recovery-state) |
| `recovered-shares.txt` contains fallback records | Preserve the source, inspect it, then use the manifested one-shot importer | [Share-recovery import](database.md#inspect-and-import-a-recovery-journal) |
| Startup says another payout manager owns the database | Prove the old process/backend is dead and reconcile every affected wallet transaction | [Payout ownership recovery](database.md#recover-payout-manager-ownership-safely) |
| Log files consume unexpected space | Check native NLog archives and remove conflicting external `copytruncate` rules | [Log rotation](configuration.md#log-files-and-rotation) |
| Relay receiver is unavailable | Restore the route and receiver; do not assume ordinary shares published during the outage will replay | [Share relays](share-relays.md#durability-boundary) |

Never clear a payout owner, delete a fatal latch, edit a recovery journal, or manually change balances
as a first response. The recovery gates exist to prevent duplicate payments and silent share loss.

## Routine maintenance

- Apply security and runtime servicing updates in a controlled maintenance window.
- Review new Miningcore release notes and database migrations before changing binaries.
- Test PostgreSQL restore and application rollback periodically, not only during an incident.
- Test administrative notifications without exposing credentials in logs or shell history.
- Review firewall rules, public ports, TLS policy, recovery-storage capacity and monitoring thresholds.
- Re-run daemon-backed or physical-relay validation after changing daemons, wallets, topology,
  firewalls, proxies or operating systems.

Windows builds remain suitable for development and test labs; Linux is the supported production
target. A laboratory pass does not replace validating the actual production route and configuration.
