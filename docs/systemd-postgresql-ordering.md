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
Use `--dry-run` to preview a selected configuration or removal without writing files or reloading systemd.
Once a unit is selected (automatically or with `--unit`), or `--remove` is requested, it reads the
current effective `Wants=`/`After=`, reports PostgreSQL dependencies needing review, and identifies
exact matches to any `--allow-remaining` options you supply. The complete informational report is
on stdout, so `> review.txt` captures it; errors remain on stderr. It works without root.
For example, before configuring a replacement cluster:

```console
/opt/miningcore/systemd/configure-postgresql-ordering.sh --dry-run \
  --unit postgresql@18-main.service --allow-remaining postgresql-exporter.service
```

This is a current-state report, not a simulation of systemd after a reload. Entries supplied by
the managed drop-in may disappear when it is replaced or removed; entries from other configuration
may survive. Review them with `systemctl cat miningcore.service` before deciding which intentional
extras to acknowledge. A valid preview returns `0` even when it lists dependencies for review;
it does not require the proposed selected unit to be in the current graph yet. Query failures
return `69`, malformed dependency properties return `78`, and neither path changes files or reloads
systemd. The actual operation still performs strict post-change verification and returns `78` for
unacknowledged extras. `--remove --dry-run` provides the same read-only report for removal.

Discovery exits before dependency preview when no operation can be selected. With zero clusters
and no managed drop-in, `--dry-run` returns `0` without querying Miningcore, even if that unit is
absent. With a stale managed drop-in or multiple clusters, it returns `78` without a graph report.
To inspect dependencies in those cases, use `--unit UNIT --dry-run` after identifying the database
unit, or `--remove --dry-run` to inspect a possible removal without removing anything.

An empty `--unit` is an error, so an unset shell variable cannot silently enable auto-discovery.

The helper requires both service units to have `LoadState=loaded` (masked, missing or unloadable
units are rejected), atomically replaces the drop-in with mode `0644`, reloads
systemd, and checks that the selected unit appears in both dependency properties before reporting
success. Configuration also rejects any additional `postgresql*` dependency from other unit
configuration unless it was explicitly acknowledged as described below. This prevents an obsolete
cluster from silently remaining alongside its replacement after an upgrade.
A reload or verification failure returns nonzero; inspect the written file and other
drop-ins, correct the problem, and rerun before enabling Miningcore. It does not roll back a file
after a failed reload. A verification failure occurs after the file has changed and systemd has
been reloaded, so the host remains in that modified state; it does not mean the operation was a
no-op. Existing drop-in directory permissions are preserved; a newly created directory uses `0755`.
Root is required for changes. The helper assumes Bash, systemd and GNU coreutils (including `mv -T`).

The `MININGCORE_SYSTEMD_ROOT` environment variable is for isolated tests with a mocked `systemctl`
and is rejected unless `MININGCORE_SYSTEMD_TEST_MODE=1` is also set. These variables do not redirect
the systemd manager or bypass the root check and must not be used as an offline installation mode.
Unset the prefix for normal operation; the second variable is an accidental-use guard, not a
security boundary or a substitute for the mock.

## Manual setup (including v0.3.0)

For v0.3.0, or a custom local service name outside the helper's supported names, identify the actual
PostgreSQL service first. The following example assumes you verified `postgresql@17-main.service`;
replace it with the unit that serves Miningcore. Run after installing and reloading
`miningcore.service`, before enabling Miningcore:

```console
systemctl cat miningcore.service postgresql@17-main.service --no-pager
sudo mkdir -p -m 0755 /etc/systemd/system/miningcore.service.d
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

For a confirmed local-to-remote migration or uninstall, stop Miningcore before changing database
topology and remove the managed relationship explicitly:

```console
sudo /opt/miningcore/systemd/configure-postgresql-ordering.sh --remove
systemctl show miningcore.service -p Wants -p After --no-pager
```

Removal touches only `/etc/systemd/system/miningcore.service.d/postgresql-ordering.conf`; other
drop-ins are preserved. After reloading, the helper checks the effective `Wants=` and `After=`:
any unacknowledged remaining token beginning with `postgresql` is reported and causes exit `78`. Inspect
`systemctl cat miningcore.service`, explicitly correct the remaining base-unit or drop-in
configuration, then rerun `--remove`. The managed file is already removed and systemd reloaded at
that point; a failed verification does not restore it. An inability to read the effective graph
also returns nonzero, rather than claiming removal succeeded. Custom database unit names outside
the `postgresql` prefix still require operator review.

An intentional dependency such as `postgresql-exporter.service` can be retained without making
every subsequent command fail. Review its purpose, then acknowledge that exact unit for this
invocation:

```console
sudo /opt/miningcore/systemd/configure-postgresql-ordering.sh --remove \
  --allow-remaining postgresql-exporter.service
```

The same option is available when configuring with `--unit`. Repeat `--allow-remaining UNIT` for
each intentional extra unit. Names are matched exactly; wildcards are rejected, acknowledgements
are not saved, and every acknowledged dependency is still reported. This option does not change
other files, suppress unexpected units, waive the selected unit's presence in both properties, or
bypass query/parse failures. Remove obsolete cluster references rather than acknowledging them.
Successful removal reports that no **unacknowledged** PostgreSQL dependencies remain; it does not
claim that intentional dependencies disappeared. Successful configuration prints its result before
the effective properties; removal omits the raw property dump.

Record each reviewed intentional dependency and its reason in comments in your own drop-in, so
the next operator can reconstruct the command during a PostgreSQL upgrade. For example, above the
existing exporter directives in `/etc/systemd/system/miningcore.service.d/monitoring.conf`:

```ini
# Reviewed dependency: postgresql-exporter.service provides local monitoring.
# Keep its existing directives; after reviewing this requirement on each upgrade,
# pass --allow-remaining postgresql-exporter.service to the ordering helper.
```

These comments are operator notes, not saved acknowledgements: the helper never reads them as
authorization. Keep them in the operator-owned drop-in, not `postgresql-ordering.conf`, which the
helper replaces. Recheck the purpose of each dependency before supplying the option again.

Removal works after either service is uninstalled and is safe to rerun, including after a failed
reload. On v0.3.0, manually update the concrete unit
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
sudo journalctl -b -1 -u miningcore -u 'postgresql*.service' --no-pager
```

The glob includes Debian/Ubuntu instances and the explicit `postgresql.service` /
`postgresql-16.service` layouts. Use the exact selected unit instead when isolating a single database.

## Remote and non-systemd PostgreSQL

Do **not** create a bogus local dependency for a remote PostgreSQL server, a container-managed database, a Kubernetes database, or another topology not controlled by the same systemd manager. Do not run auto-configuration merely because an unrelated local cluster exists. A fresh host with no matching active cluster is skipped; an existing managed drop-in makes that same discovery result an error requiring operator action.

For a migration from local to remote PostgreSQL, use the explicit removal procedure above. If
PostgreSQL is local but uses a custom service name, use the manual setup with that actual local unit
and verify the dependency graph explicitly.

## Recovery remains fail-closed

If Miningcore reports that a durable payout-manager ownership marker remains without its advisory lock, do not delete or clear it merely to make startup succeed. Confirm the recorded process/backend is dead, reconcile any wallet submission with an uncertain outcome, and then follow the guarded payout-manager ownership recovery procedure in [Database and recovery](database.md#recover-payout-manager-ownership-safely).

## Regression validation

The `.NET` CI workflow runs the mocked lifecycle suite with root privileges; locally it requires
`sudo bash scripts/release/test-systemd-postgresql-ordering.sh` and `setpriv` from util-linux.
The fixture prefix and mocked manager keep its mutations inside a private temporary directory.
The CI runner also runs `test-systemd-postgresql-ordering-integration.sh --disposable-host` against
its real systemd manager. That test creates inert PostgreSQL/Miningcore service fixtures, checks
startup and reverse shutdown order and captures journal events. A disposable target and fixture-only
`PartOf=` relationships place both stops in one transaction, avoiding reliance on multi-unit
`systemctl stop` batching across systemd versions. The test also checks surviving-dependency,
explicit-acknowledgement and masked-unit handling, plus real
auto-discovery in dry-run mode for the runner's current zero/one/multiple active-cluster state;
the inert ordering fixtures use explicit selection so existing runner databases are never changed.
Cleanup preserves the original test failure status while reporting cleanup errors separately.
It refuses pre-existing Miningcore service
configuration and cleans up its own files. Run it only as root on a disposable systemd host.
