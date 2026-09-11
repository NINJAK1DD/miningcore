# systemd ordering for local PostgreSQL

Miningcore's payout manager deliberately fails closed when its PostgreSQL guard session disappears before the durable payout-manager ownership record can be released. That protects against duplicate or uncertain wallet submissions, but it also means an avoidable shutdown-ordering race can leave a stale ownership marker after a normal host reboot.

For installations where Miningcore and PostgreSQL run on the same systemd host, order the services so PostgreSQL starts before Miningcore and stops after Miningcore. This gives Miningcore time to stop its pools, finish payout/recovery cleanup and release its durable ownership before PostgreSQL terminates database sessions.

This is an operational hardening measure, not a replacement for payout-owner recovery. Power loss, kernel panic, forced reset, manual PostgreSQL termination, database crashes and remote-database failures can still leave fail-closed recovery state.

## Packaged helper

Release archives include:

```text
/opt/miningcore/systemd/configure-postgresql-ordering.sh
```

Run it after installing `miningcore.service` and before enabling the service:

```console
sudo /opt/miningcore/systemd/configure-postgresql-ordering.sh
```

The helper discovers **running** `postgresql@*.service` cluster units and behaves conservatively:

- exactly one cluster: configure it automatically;
- no matching cluster: make no change and report that remote/non-standard PostgreSQL may be intentional;
- multiple clusters: refuse to guess and require an explicit `--unit` selection.

For a host with multiple local clusters, identify the one used by Miningcore:

```console
systemctl list-units --type=service --state=running 'postgresql@*.service' --no-legend --plain
```

Then configure it explicitly, for example:

```console
sudo /opt/miningcore/systemd/configure-postgresql-ordering.sh \
  --unit postgresql@17-main.service
```

Do not copy that example unit name blindly. PostgreSQL major versions and cluster names vary by distribution and host.

## Resulting drop-in

The helper writes:

```text
/etc/systemd/system/miningcore.service.d/postgresql-ordering.conf
```

with the concrete detected unit name:

```ini
[Unit]
Wants=postgresql@17-main.service
After=postgresql@17-main.service
```

Systemd does not expand ordinary service `Environment=` variables inside `Wants=` or `After=`, so the concrete unit must be written into the drop-in rather than referenced through a shell-style variable.

`After=` provides the important ordering in both directions:

```text
Startup:   PostgreSQL -> Miningcore
Shutdown:  Miningcore -> PostgreSQL
```

`Wants=` pulls the selected local PostgreSQL cluster into the same startup transaction without using the stronger `Requires=` coupling. A later manual or abnormal PostgreSQL failure still relies on Miningcore's existing fail-closed behaviour.

## Verification

After configuration, verify the dependency graph:

```console
systemctl show miningcore -p Wants -p After --no-pager
systemctl cat miningcore
```

The selected `postgresql@...service` should appear in both `Wants=` and `After=` and the generated drop-in should be visible in `systemctl cat` output.

A `daemon-reload` is performed by the helper. If Miningcore is already running, the dependency applies to subsequent systemd start/stop/reboot transactions; an immediate Miningcore restart is not required solely to register the ordering.

At the next planned reboot, confirm the previous-boot journal shows Miningcore completing its shutdown before PostgreSQL stops:

```console
sudo journalctl -b -1 -u miningcore -u 'postgresql@*.service' --no-pager
```

## Remote and non-systemd PostgreSQL

Do **not** create a bogus local dependency for a remote PostgreSQL server, a container-managed database, a Kubernetes database, or another topology not controlled by the same systemd manager. The helper intentionally skips when no running local `postgresql@*.service` cluster is detected.

If PostgreSQL is local but uses a custom service name, do not force it through this helper. Create an equivalent reviewed drop-in using that actual local unit and verify the dependency graph explicitly.

## Recovery remains fail-closed

If Miningcore reports that a durable payout-manager ownership marker remains without its advisory lock, do not delete or clear it merely to make startup succeed. Confirm the recorded process/backend is dead, reconcile any wallet submission with an uncertain outcome, and then follow the guarded payout-manager ownership recovery procedure in [Database and recovery](database.md#recover-payout-manager-ownership-safely).
