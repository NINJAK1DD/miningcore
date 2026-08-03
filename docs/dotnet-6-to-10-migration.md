# Migrating an existing Miningcore deployment from .NET 6 to .NET 10

This guide gives operators a safe general sequence for moving an existing Miningcore installation
to the .NET 10 release. Miningcore deployments vary, so adapt paths, service names, users, ports and
database commands to your environment. Read the complete release notes and inspect the changes
between your current commit and the target release before the maintenance window.

The migration has separate but related parts:

1. install a supported .NET 10 runtime or choose the versioned .NET 10 container image;
2. deploy Miningcore binaries built for `net10.0`;
3. reconcile the existing configuration with the new example;
4. apply every required database migration; and
5. switch traffic only after a controlled validation.

Installing .NET 10 alone does not upgrade a .NET 6 Miningcore binary. Likewise, a `net10.0`
framework-dependent build will not start until the .NET 10 ASP.NET Core runtime is available.

## Supported starting point

The tested prebuilt archive targets **Ubuntu 22.04 x64**. Ubuntu 24.04, Debian 12 and Windows
development use the source-build instructions in the root README. Other distributions, CPU
architectures, custom containers, self-contained publications and orchestration systems need an
equivalent operator-owned procedure.

Do not combine an operating-system upgrade, database-server upgrade and Miningcore runtime upgrade
in one irreversible change. If the host operating system is unsupported, prefer preparing a new
host and moving traffic after validation.

## 1. Inventory the live deployment

Record enough information to reproduce the current service before changing it. The following
commands are examples for a systemd host; substitute the actual service name:

```console
sudo systemctl status miningcore --no-pager
sudo systemctl cat miningcore
sudo systemctl show miningcore -p User -p Group -p WorkingDirectory -p ExecStart
command -v dotnet
readlink -f "$(command -v dotnet)"
dotnet --info
dotnet --list-runtimes
dotnet --list-sdks
uname -m
. /etc/os-release && printf '%s %s\n' "$ID" "$VERSION_ID"
```

Also record:

- the exact Miningcore commit or release and how it was built;
- the application, configuration, log, recovery-journal and writable-state paths;
- the systemd unit or container definition, environment variables and secrets mechanism;
- PostgreSQL location, schema version, backup and restore commands;
- daemon, wallet, ZeroMQ, share-relay, API and reverse-proxy endpoints; and
- which single node owns payment processing for each database/pool set.

Inspect the installed .NET packages and APT sources before adding another source:

```console
dpkg-query -W -f='${db:Status-Abbrev}\t${binary:Package}\t${Version}\n' \
  'dotnet*' 'aspnetcore*' 'netstandard*' 2>/dev/null | awk '$1 ~ /^ii/ {print}'
grep -RhsE 'packages\.microsoft\.com|ppa\.launchpadcontent\.net/.*/dotnet' \
  /etc/apt/sources.list /etc/apt/sources.list.d 2>/dev/null || true
apt-cache policy aspnetcore-runtime-10.0 dotnet-runtime-10.0 dotnet-sdk-10.0
```

Canonical warns against mixing .NET installation methods because runtime resolution and servicing
can become unreliable. Determine whether the current .NET 6 installation came from Ubuntu, a PPA,
Microsoft packages, Snap, `dotnet-install.sh`, a manual archive or a container. Use one deliberate
method for .NET 10 and do not delete a package-managed installation by hand. Filtering on the
`ii` status is important: an unfiltered `dpkg-query` wildcard can include packages known to the
package database that are not currently installed.

## 2. Prepare rollback and backups

Before stopping the service:

- make and test a PostgreSQL backup;
- copy the active Miningcore configuration, service/container definition, environment files,
  certificates and recovery journals to protected storage;
- retain the complete old application directory or immutable old container image;
- record the current symlink target and package versions; and
- decide who can abort the change and how traffic will be returned to the old instance.

Treat database rollback separately from application rollback. Some migrations are not reversible by
running an older binary. Restoring the old application can therefore also require restoring the
matching database backup. Never run old and new payout managers concurrently against the same
database.

## 3. Stage .NET 10 without removing .NET 6

Keep .NET 6 in place during the first deployment. Major .NET runtimes can normally exist side by
side, which preserves application rollback while the new service is validated. Remove .NET 6 only
after checking that no other application on the host requires it.

### Ubuntu 22.04

The tested APT path uses Canonical's .NET backports PPA:

> These commands assume that Canonical packages are the chosen .NET installation method. If the
> inventory shows an existing Microsoft feed or manual installation, do not blindly add the PPA.
> Reconcile the package sources using the official Ubuntu guidance first, or prepare a clean host.
> After installation, use `apt-cache policy` to confirm that the .NET 10 host, runtime and ASP.NET
> Core packages come from the intended source.

```console
sudo apt-get update
sudo apt-get install -y software-properties-common
sudo add-apt-repository -y ppa:dotnet/backports
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-10.0 libgmp10 libsodium-dev libzmq3-dev
```

### Ubuntu 24.04

.NET 10 is available from Ubuntu's Canonical-maintained package feed; do not add Microsoft's Ubuntu
feed or combine it with Ubuntu .NET packages:

```console
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-10.0 libgmp10 libsodium-dev libzmq3-dev
```

Only source-build hosts need `dotnet-sdk-10.0` and the full compiler/native toolchain. A host running
the framework-dependent release archive needs the ASP.NET Core runtime, not the SDK.

Confirm that both runtime families are visible during the rollback window:

```console
dotnet --list-runtimes
dotnet --info
```

The output must contain `Microsoft.AspNetCore.App 10.*` and `Microsoft.NETCore.App 10.*` before the
new Miningcore binary is started.

## 4. Stage the new application separately

Do not publish or extract over the running application directory. Use a new versioned directory and
keep a stable symlink or an equivalent atomic deployment pointer:

```text
/opt/miningcore/releases/old-dotnet6/
/opt/miningcore/releases/new-dotnet10/
/opt/miningcore/current -> /opt/miningcore/releases/old-dotnet6/
```

Choose one deployment path:

- **Release archive:** follow [Installing a prebuilt Miningcore release](releases.md). Verify
  `SHA256SUMS` and provenance before extracting it.
- **Source build:** use a clean checkout of the target commit and the matching supported build script.
  Publish to a new empty directory; never reuse a .NET 6 `bin`, `obj` or publish directory.
- **Container:** pull and pin the exact versioned .NET 10 image. Preserve the old immutable image and
  deployment definition. Review the release guide's non-root UID/GID and bind-mount requirements.

Keep the existing production configuration outside the application directory. Do **not** replace it
with `config.example.json`. Instead, compare the old file with the target release's example and
merge intentional additions or renamed settings while retaining pool, daemon, wallet, database,
relay and notification values. The supplied file is JSON with comments, so `jq empty` is not an
authoritative validator and can reject a configuration Miningcore accepts. Use a JSON-with-comments
aware editor with `src/Miningcore/config.schema.json`, then validate the staged configuration on an
isolated test instance before it can own payouts or accept production miners.

Verify the staged application and native library loader without starting mining:

```console
cd /opt/miningcore/releases/new-dotnet10
LD_LIBRARY_PATH="$PWD" dotnet Miningcore.dll --version
missing="$(
  find . -maxdepth 1 -type f \( -name Miningcore -o -name '*.so' \) -exec ldd {} \; |
  grep 'not found' || true
)"
if [ -n "$missing" ]; then
  echo "STOP: missing native dependencies"
  echo "$missing"
else
  echo "OK: all native dependencies resolved"
fi
```

The version command must report the intended release/commit, and `ldd` must report no missing
dependencies. Native libraries built on a newer distribution may not run on an older host; build on
the oldest supported target or use the tested Ubuntu 22.04 release archive.

For a source build, verify the checkout and the resulting binary together before deployment:

```console
cd ~/miningcore
git describe --tags --exact-match HEAD
git rev-parse HEAD
./build/Miningcore --version
```

The first command must print the intended tag and the second its commit. Releases published after
the version-reporting validation was introduced must report the same semantic version (without the
tag's leading `v`) and full commit SHA. Older releases can show the legacy `0.1.0.0-BRANCH` format;
for those builds, match the full embedded SHA to the tagged commit. If the exact-tag command fails,
do not describe the build as that release even when the commit is otherwise on `dev`.

The supported Linux build scripts inject this release identity only when `HEAD` has exactly one
SemVer release tag and the checkout is clean. Untagged branch builds retain GitVersion's calculated
prerelease version. A dirty or ambiguously tagged release checkout is rejected so locally modified
code cannot be labelled as an official release.

## 5. Stop writers and migrate the database

Schedule a maintenance window. Stop Miningcore on every node that writes shares, blocks, payments or
recovery imports to the affected database, then confirm that no process remains:

```console
sudo systemctl stop miningcore
sudo systemctl is-active miningcore
pgrep -af 'Miningcore|Miningcore.dll' || true
```

Stop Miningcore before stopping or restarting PostgreSQL. A clean application shutdown releases
its durable payout-manager owner; taking PostgreSQL down first destroys the advisory-lock session
and intentionally leaves the marker fail-closed. If a marker remains, follow the guarded
[payout-manager ownership recovery](database.md#recover-payout-manager-ownership-safely) instead of
restarting repeatedly or clearing it without inspecting the previous process and wallet history.

Follow [Database setup and upgrades](database.md) and apply every migration required between the old
commit and the target release with `ON_ERROR_STOP`. Do not run `createdb.sql` over an existing
database. The current release requires payout-manager ownership changes for payment-processing and
recovery-import deployments; merged-mining deployments have additional required migrations.

## 6. Switch and validate

Update the stable symlink and service unit only after the runtime, application, configuration and
database are ready. If adopting the packaged systemd unit, compare it with the existing unit first;
preserve required environment variables and custom dependencies while retaining the packaged
security settings and shutdown allowance.

```console
sudo ln -sfn /opt/miningcore/releases/new-dotnet10 /opt/miningcore/current
sudo systemctl daemon-reload
sudo systemctl start miningcore
sudo systemctl status miningcore --no-pager
sudo journalctl -u miningcore --since '10 minutes ago' --no-pager
```

Miningcore now delegates TLS protocol selection to the operating system. On supported, patched
hosts this means TLS 1.2 or TLS 1.3; miners limited to TLS 1.0 or TLS 1.1 can no longer connect to a
TLS-enabled Stratum endpoint. Inventory legacy mining hardware before the maintenance window and
test at least one representative client against every TLS-enabled endpoint.

Before returning normal traffic, confirm:

- the service stays active and reports the intended Miningcore commit;
- every enabled pool reaches daemon and wallet endpoints and completes synchronization;
- PostgreSQL writes succeed without schema-preflight errors;
- exactly the intended node owns payment processing;
- Stratum authentication and a low-risk test share work;
- share relay and recovery journaling work if the deployment uses them; and
- API, metrics, reverse proxy, firewall and log paths behave as before.

Keep the old application and .NET 6 runtime for an observation period appropriate to the pool's
risk, payout interval and operational change policy.

## 7. Roll back if validation fails

Stop the .NET 10 process before starting the old one. If no incompatible database migration was
applied, repoint the deployment symlink or restore the previous container definition and start the
old service. If the database changed incompatibly, restore the matching backup according to the
release-specific migration notes.

Do not use repeated automatic restarts as a migration strategy. Capture the complete journal and
rollback if the failure cannot be corrected safely within the maintenance window.

## 8. Retire .NET 6 later

After the new deployment has completed its observation period, inventory every application on the
host again. If nothing needs .NET 6, remove it using the same installation mechanism that installed
it. For APT-managed packages, preview the transaction before approving it:

```console
sudo apt-get -s remove aspnetcore-runtime-6.0 dotnet-runtime-6.0 dotnet-sdk-6.0
```

Review the proposed removals carefully. Do not remove the shared `dotnet-host` when it is required by
.NET 10, and do not manually delete files owned by APT, Snap or another package manager. Finish by
checking `dotnet --list-runtimes`, starting Miningcore once more, and recording the final package and
application versions.

## Further reading

- [Canonical: set up .NET on Ubuntu](https://ubuntu.com/developers/docs/howto/dotnet-setup/)
- [Canonical: available .NET versions](https://ubuntu.com/developers/docs/reference/availability/dotnet/)
- [Microsoft: choose and troubleshoot .NET packages on Ubuntu](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu-decision)
- [Microsoft: remove .NET runtimes and SDKs](https://learn.microsoft.com/dotnet/core/install/remove-runtime-sdk-versions)
- [Microsoft: .NET 10 breaking changes](https://learn.microsoft.com/dotnet/core/compatibility/10/)
