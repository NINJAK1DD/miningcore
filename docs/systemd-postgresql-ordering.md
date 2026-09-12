# systemd ordering for local PostgreSQL

Miningcore's payout manager deliberately fails closed when its PostgreSQL guard session disappears before the durable payout-manager ownership record can be released. That protects against duplicate or uncertain wallet submissions, but it also means an avoidable shutdown-ordering race can leave a stale ownership marker after a normal host reboot.

For installations where Miningcore and PostgreSQL run on the same systemd host, order the services so PostgreSQL starts before Miningcore and stops after Miningcore. This gives Miningcore time to stop its pools, finish payout/recovery cleanup and release its durable ownership before PostgreSQL terminates database sessions.

This is an operational hardening measure, not a replacement for payout-owner recovery. Power loss, kernel panic, forced reset, manual PostgreSQL termination, database crashes and remote-database failures can still leave fail-closed recovery state.

## Packaged helper

Archives from the release containing this change onward include the helper and example below.
The v0.3.0 archives predate this change and contain neither file; use the
[manual setup](#manual-setup-including-v030) for that release. A source checkout containing this
change provides the helper at `packaging/systemd/configure-postgresql-ordering.sh`.

```text
/opt/miningcore/systemd/configure-postgresql-ordering.sh
/opt/miningcore/systemd/postgresql-ordering.conf.example
```

For a database on the same systemd host, run it after installing `miningcore.service` and running
`sudo systemctl daemon-reload`, and before enabling Miningcore. Check that the selected local unit
actually serves Miningcore's configured database; discovery cannot infer the database connection.
For a remote database, follow [remote deployment guidance](#remote-and-non-systemd-postgresql).

```console
sudo /opt/miningcore/systemd/configure-postgresql-ordering.sh
```

The helper discovers **active** Debian/Ubuntu `postgresql@*.service` cluster units (including the
`exited` sub-state) and behaves conservatively:

- exactly one cluster: configure it automatically;
- no matching cluster, no existing managed drop-in: skip and explain the discovery scope;
- no matching cluster with an existing managed drop-in: fail without changing it; select the
  replacement cluster or explicitly remove the old ordering;
- multiple clusters: refuse to guess and require an explicit `--unit` selection.

For a host with multiple local clusters, identify the one used by Miningcore:

```console
systemctl list-units --type=service --state=active 'postgresql@*.service' --no-legend --plain --no-pager
```

Then configure it explicitly, for example:

```console
sudo /opt/miningcore/systemd/configure-postgresql-ordering.sh \
  --unit postgresql@17-main.service
```

Do not copy that example unit name blindly. PostgreSQL major versions and cluster names vary by distribution and host.

For standard Fedora/RHEL layouts, select the actual installed unit explicitly, for example
`--unit postgresql.service` or `--unit postgresql-16.service`. Those layouts are not auto-discovered.
Use `--dry-run` to preview any configuration or removal without writing files or reloading systemd.
An empty `--unit` is an error, so an unset shell variable cannot silently enable auto-discovery.

The helper validates both service units, atomically replaces the drop-in with mode `0644`, reloads
systemd, and checks that the selected unit appears in both dependency properties before reporting
success. A reload or verification failure returns nonzero; inspect the written file and other
drop-ins, correct the problem, and rerun before enabling Miningcore. It does not roll back a file
after a failed reload. Root is required for changes. The `MININGCORE_SYSTEMD_ROOT` environment
variable is for isolated tests with a mocked `systemctl`; it does not redirect the systemd manager
or bypass the root check and must not be used as an offline installation mode.

## Manual setup (including v0.3.0)

For v0.3.0, or a custom local service name outside the helper's supported names, identify the actual
PostgreSQL service first. The following example assumes you verified `postgresql@17-main.service`;
replace it with the unit that serves Miningcore. Run after installing and reloading
`miningcore.service`, before enabling Miningcore:

```console
systemctl cat miningcore.service postgresql@17-main.service --no-pager
sudo install -d -m 0755 /etc/systemd/system/miningcore.service.d
sudo tee /etc/systemd/system/miningcore.service.d/postgresql-ordering.conf >/dev/null <<'EOF'
[Unit]
Wants=postgresql@17-main.service
After=postgresql@17-main.service
EOF
sudo chmod 0644 /etc/systemd/system/miningcore.service.d/postgresql-ordering.conf
sudo systemctl daemon-reload
systemctl show miningcore.service -p Wants -p After --no-pager
```

Continue only if both properties contain the selected service. This procedure requires no files
from a future release. For releases containing the helper, prefer its validated setup above.

## PostgreSQL upgrades and removal

Rerun the helper whenever the PostgreSQL major version or cluster unit name changes. Stop Miningcore
before the database upgrade, start and verify the replacement cluster, then rerun with its explicit
`--unit` before starting Miningcore. For example, upgrading `17-main` to `18-main` requires selecting
`postgresql@18-main.service`; verify that both `Wants=` and `After=` reference the replacement.
If multiple clusters are active the helper refuses to guess, preserving the existing file. A
concrete old unit name does not update itself when PostgreSQL is upgraded.

For a confirmed local-to-remote migration or uninstall, remove the managed relationship explicitly:

```console
sudo /opt/miningcore/systemd/configure-postgresql-ordering.sh --remove
systemctl show miningcore.service -p Wants -p After --no-pager
```

Removal touches only `/etc/systemd/system/miningcore.service.d/postgresql-ordering.conf`; other
drop-ins are preserved and may still add dependencies. It works after either service is uninstalled
and is safe to rerun, including after a failed reload. On v0.3.0, manually update the concrete unit
in that file after an upgrade, or remove that file for a confirmed remote migration, then run
`sudo systemctl daemon-reload` and verify the remaining dependencies. Do not delete the entire
drop-in directory.

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

The selected PostgreSQL service should appear in both `Wants=` and `After=` and the generated drop-in should be visible in `systemctl cat` output.

A `daemon-reload` is performed by the helper. If Miningcore is already running, the dependency applies to subsequent systemd start/stop/reboot transactions; an immediate Miningcore restart is not required solely to register the ordering.

At the next planned reboot, confirm the previous-boot journal shows Miningcore completing its shutdown before PostgreSQL stops:

```console
sudo journalctl -b -1 -u miningcore -u 'postgresql@*.service' --no-pager
```

## Remote and non-systemd PostgreSQL

Do **not** create a bogus local dependency for a remote PostgreSQL server, a container-managed database, a Kubernetes database, or another topology not controlled by the same systemd manager. Do not run auto-configuration merely because an unrelated local cluster exists. A fresh host with no matching active cluster is skipped; an existing managed drop-in makes that same discovery result an error requiring operator action.

For a migration from local to remote PostgreSQL, use the explicit removal procedure above. If
PostgreSQL is local but uses a custom service name, use the manual setup with that actual local unit
and verify the dependency graph explicitly.

## Recovery remains fail-closed

If Miningcore reports that a durable payout-manager ownership marker remains without its advisory lock, do not delete or clear it merely to make startup succeed. Confirm the recorded process/backend is dead, reconcile any wallet submission with an uncertain outcome, and then follow the guarded payout-manager ownership recovery procedure in [Database and recovery](database.md#recover-payout-manager-ownership-safely).
