# Dependency security

Miningcore restores the complete transitive package graph with `NuGetAuditMode=all` and an explicit
`NuGetAuditLevel=low`. With `TreatWarningsAsErrors=true`, every audit finding (`NU1901` through
`NU1904`) fails restore. `NU1903` and `NU1904` are also listed in `WarningsAsErrors`, so high and
critical findings remain fatal even if a local build sets `TreatWarningsAsErrors=false`.

Miningcore adopts this stricter policy. It supersedes the earlier decision to leave low and moderate
findings as warnings. Enabling compiler warnings as errors in
[PR #104](https://github.com/NINJAK1DD/miningcore/pull/104) also made these audit findings fatal.
A newly published advisory of any severity blocks builds and releases until the dependency is
upgraded or a scoped, written risk acceptance is recorded as described below.

Run the same audit explicitly with:

```console
dotnet list src/Miningcore.sln package --vulnerable --include-transitive
```

Upgrade or replace an affected dependency whenever possible. If an immediate upgrade is not possible,
record the affected code path, deployment exposure, compensating controls, owner and removal condition
in the pull request or a tracking issue. A narrowly scoped `NuGetAuditSuppress` for the advisory URL may
be used only with that written risk acceptance; do not disable NuGet auditing or globally suppress the
`NU190x` warning family.

## PKCS servicing override

Miningcore references `System.Security.Cryptography.Pkcs` directly to keep normal dependency
resolution at or above 10.0.13, the first .NET 10 package with the fixes for
[CVE-2026-70162](https://github.com/dotnet/announcements/issues/454) and
[CVE-2026-84008](https://github.com/dotnet/announcements/issues/468).
No audit suppression or severity downgrade is required.

For this policy, the **servicing baseline** is the highest stable `10.0.x` version among the
`Microsoft.Extensions.*` references in [`Miningcore.csproj`](../src/Miningcore/Miningcore.csproj),
which also holds the reviewed PKCS minimum version. NuGet's plain `Version` value is an
[inclusive minimum](https://learn.microsoft.com/en-us/nuget/concepts/package-versioning#version-ranges),
so verify the resolved package and loaded assembly rather than assuming an exact version. For
removal decisions, calculate the baseline from the removal PR's base revision.

NuGet normally selects the
[lowest applicable version and gives direct references precedence][nuget-resolution];
if a transitive requirement exceeds the selected direct PKCS version,
[NU1605][nuget-downgrade] fails Miningcore's normal restore.

[nuget-resolution]:
  https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution#dependency-resolution-rules
[nuget-downgrade]:
  https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1605

[Dependabot](../.github/dependabot.yml) groups monthly patch version updates for these packages and
PKCS in `dotnet-servicing` and ignores their major-version updates. Patch updates remain eligible.

For update PRs:

- Keep PKCS at or above the baseline and the 10.0.13 security floor. Check grouped, manual and
  security updates against Microsoft's current advisories too; this maintenance convention does
  not establish the latest secure PKCS or runtime version.
- Review `MiningcoreRuntimeMinimum` in
  [`RuntimeDependencyPolicy.props`](../src/Miningcore/RuntimeDependencyPolicy.props) with every
  servicing update. Framework-only PKCS needs that floor at least as high as the reviewed PKCS
  reference; Dependabot's package group does not update this property. Raise it when needed,
  then rebuild and validate both deployment frameworks. Maintainers must recheck Microsoft's
  runtime advisories before each release; the static floor is not an automatic security feed.
- If no stable PKCS `10.0.x` release meets the baseline, retain the highest available stable
  `10.0.x` release, never below 10.0.13 or the already-reviewed minimum. Record dated Microsoft/NuGet
  release-availability evidence and recheck at the next dependency update. This exception cannot
  authorize reference removal or bypass vulnerability auditing.
- Moving to a new .NET major version requires updating this policy, its baseline definition,
  the Dependabot rules and runtime evidence requirements together in the same migration PR.

For removal PRs:

- Require restore and a complete vulnerable-package audit to show every transitive path resolves
  PKCS at or above both 10.0.13 and the baseline without the reference. Record the base revision,
  calculated baseline and resolved package version; the CVE fix floor alone cannot justify a
  servicing downgrade.
- Record the loaded PKCS assembly's informational version and path, and the selected .NET and
  ASP.NET shared-framework versions under the published application's dependency manifest and
  runtime configuration.

Miningcore publishes framework-dependent, so PKCS can load from `Microsoft.AspNetCore.App` instead
of the package copy; the package version alone does not establish which assembly runs. Keep the
deployed shared frameworks patched.

## AutoMapper 16 licence decision

Miningcore upgrades AutoMapper 12.0.1 to 16.2.0 because the previous version is affected by
GHSA-rvv3-g6hj-g44x and no patched release exists before the AutoMapper 15 line. Version 16.2.0 is on a
patched line and supports the standard environment-variable licence discovery documented in the
README.

AutoMapper's upstream licence states that its source and binaries are governed by the Reciprocal Public
License 1.5 (RPL-1.5), unless they are used under the upstream commercial licence agreement. Miningcore
maintainers accept including AutoMapper 16.2.0 under those upstream terms to replace the vulnerable
version while preserving the existing mapping behavior. This records the dependency decision; it does
not determine which option applies to a particular downstream deployment or provide legal advice.

Operators and redistributors must review the upstream terms and determine their own compliance before
using this dependency. Miningcore's licence does not replace AutoMapper's terms. If neither upstream
option is acceptable for the project's future distribution or deployment requirements, replace
AutoMapper with explicit mapping code; reverting to an affected release is not an accepted fallback.

Decision owner: Miningcore repository maintainers. Reassess this dependency during each major security
or dependency refresh and before any change to Miningcore's distribution model.

## Legacy NBitcoin.Zcash dependency

`NBitcoin.Zcash` 3.0.0 depends on the deprecated `Portable.BouncyCastle` package. Miningcore pins version
1.9.0 as a runtime-only asset so `NBitcoin.Zcash` can resolve `BouncyCastle.Crypto.dll`, while Miningcore
source compiles exclusively against the maintained `BouncyCastle.Cryptography` package.

The upstream maintainer described the 1.9.x line as end-of-life after 2025 and said further maintenance
would be discretionary. NuGet currently reports a clean graph, but that is not proof that the legacy
assembly is patched: some Bouncy Castle advisories are indexed under the `BouncyCastle` and
`BouncyCastle.Cryptography` package IDs rather than `Portable.BouncyCastle`.

This is an explicit compatibility risk acceptance, not a declaration that the legacy package is secure.
The release gate verifies that both assemblies are present and executes the NBitcoin.Zcash BLAKE2b
binding under Miningcore's published dependency manifest. That test detects packaging and type-binding
regressions; it does not remove the security risk of an unsupported cryptography library.

Risk owner: Miningcore repository maintainers. Reassess this acceptance during every major dependency
or security refresh and track replacement work when a viable NBitcoin.Zcash migration path exists.

Remove `Portable.BouncyCastle` when `NBitcoin.Zcash` is upgraded, replaced, or changed so it no longer
requires the legacy assembly. Until then, keep the dependency runtime-only and do not use its
`Org.BouncyCastle.*` types from Miningcore source.

References:

- [AutoMapper 16.2.0 upstream licence](https://github.com/LuckyPennySoftware/AutoMapper/blob/dfa6dd587c5854b4beee5934beb39ba6e9569b84/LICENSE.md)
- [AutoMapper uncontrolled-recursion advisory](https://github.com/advisories/GHSA-rvv3-g6hj-g44x)
- [AutoMapper licence configuration](https://docs.automapper.io/en/stable/License-configuration.html)
- [BouncyCastle.Crypto 1.9.0 lifecycle statement](https://github.com/bcgit/bc-csharp/discussions/450)
- [Example advisory package-ID mapping](https://github.com/advisories/GHSA-v435-xc8x-wvr9)
- [NuGet audit warnings](https://learn.microsoft.com/nuget/reference/errors-and-warnings/nu1901-nu1904)

## Published dependency and runtime acceptance

After a normal framework-dependent release publish, run the existing release gate:

```console
bash scripts/release/validate-managed-dependencies.sh publish
```

The gate compares the direct PKCS reference with the highest stable `10.0.x`
`Microsoft.Extensions.*` reference and the security floor. A patched validation host cannot
compensate for a project reference below that baseline. An unavailable-release exception requires
an explicit review of the gate alongside the dated policy evidence; it is not a CLI bypass.

For application-local PKCS, the gate requires the reviewed version in both the manifest library
inventory and active runtime target, its runtime asset and DLL. SDK publish conflict resolution
can instead omit that asset in favor of `Microsoft.AspNetCore.App`; this is distinct from NuGet
package pruning. Framework-only output must declare an ASP.NET minimum at least as high as PKCS,
with no untracked local DLL. Both framework minima must meet the runtime policy.

[`RuntimeDependencyPolicy.props`](../src/Miningcore/RuntimeDependencyPolicy.props) is the common
source of the .NET/ASP.NET servicing floor and PKCS security floor. Publishing sets
`RuntimeFrameworkVersion` for Core and the known ASP.NET runtime versions before SDK resolution,
so both minima come from that policy rather than the installed SDK's default. ASP.NET remains a
transitive framework reference, preserving NuGet's package-pruning behavior. The gate checks the emitted
runtimeconfig, not just these build properties. The host refuses older frameworks. Normal
Miningcore startup also checks the selected Core runtime, an actual ASP.NET shared-framework
assembly and PKCS before opening connections, including when runtimeconfig settings were changed.
The reviewed PKCS minimum is embedded from the package reference at build time; startup enforces
it as well as the security floor. A missing or damaged policy requires reinstalling the complete
reviewed release. Configuration dumps and read-only share-recovery verification remain offline;
see the diagnostic procedure below when the host itself refuses the shipped runtimeconfig.
Self-contained builds and publishes are explicitly rejected: these policy values are deployment
minima, not approval to bundle that runtime patch. Supporting self-contained artifacts requires a
separate bundled-runtime servicing policy and release validation.

The smoke prints both selected framework versions/locations and PKCS's informational version and
location under Miningcore's published manifest/runtimeconfig. Package mode accepts the exact
application copy or an equal-or-newer copy from the selected ASP.NET framework. Framework mode
requires that selected framework's copy. Installed-runtime inventories are additional diagnostics,
not proof of selection. The same checks run against Windows publishing and inside both source and
packaged container images, as their normal unprivileged user. NBitcoin.Zcash binding is also tested.
Reported versions and locations do not authenticate binaries. Continue verifying release provenance,
published checksums and installation permissions independently of the runtime guard.
Linux and Windows negative tests use hash-verified 10.0.14 runtime archives in separate disposable
directories. They never install an older SDK into the publishing toolchain or change its `PATH`.
Network, TLS, HTTP-protocol and response-read failures receive at most three attempts with 2- and
4-second backoff. Local archive open, write and flush failures stop immediately, as do checksum
mismatches, archive-size violations and permanent HTTP failures.
Each archive is capped at 128 MiB. A separate download worker has a 300-second total deadline
covering all attempts, backoff and checksum verification; it is terminated before cleanup on timeout,
and extraction cannot begin unless it succeeds. Windows CI explicitly selects
Python 3.13 rather than relying on the runner's default Python.
An unavailable pinned archive fails CI; the test is never silently skipped. If upstream availability
becomes a recurring problem, maintainers can review a controlled mirror that preserves the exact
archive hashes, transfer limits and mandatory validation before enabling it.

Run the portable gate on Windows with:

```console
python scripts/release/run-managed-dependency-smoke.py build/windows-x64
```

### Deployment when distribution packages lag

Keep both shared frameworks patched to the latest supported .NET 10 servicing release, at least
the common policy floor, raised to 10.0.15 for Microsoft's 10 November 2026 security servicing
release. The Microsoft SDK lanes select 10.0.404; the Ubuntu 24.04/26.04 source lanes use
Canonical's separately maintained SDK feature band (10.0.115 as of 11 November 2026).
The reviewed PKCS and Microsoft.Extensions references are 10.0.15.
Both container Dockerfiles use reviewed version tags with immutable manifest-list
SHA-256 digests from Microsoft's registry. The source SDK tag matches the CI SDK; the runtime
tag may advance independently to any stable `10.0.x` at or above the host minimum. Advancing a
container patch does not by itself require raising every operator's host minimum. Both runtime
image references must agree. The floating `10.0-resolute` alias still served 10.0.14 during the
10 November rollout, so it is not used to select the reviewed base.

Dependabot checks both Dockerfile directories weekly for patch and digest updates, grouping the
same image across directories. New .NET major/minor lines require deliberate policy review.
Its Docker dependency names omit the registry: the allow rules use `dotnet/sdk` and
`dotnet/aspnet`, and the group and ignore rules use `dotnet/*`. Fully qualified MCR names do not
match the current updater ([upstream matching issue][dependabot-docker-names]). The container
policy test checks these selectors against both Dockerfiles. After activating this configuration,
check the first hosted Dependabot update log for discovery of both names and both runtime-image
locations; the local selector test does not verify the hosted updater. If a grouped PR cannot rebase
with `unknown group` ([upstream rebase issue][dependabot-docker-rebase]), recreate the update or
apply it manually from current `dev`, keeping both runtime references aligned and all gates enabled.
Review these PRs promptly: immutable bases do not absorb later servicing or OS-image changes on
rebuild. For a manual update, inspect the intended version tag with
`docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0.15-resolute` (substitute the
reviewed version, or the SDK image), and record its top-level `Digest` after verifying the registry
and release. Update the `tag@sha256:...` references together and rerun the container policy and
actual-runtime checks. SDK image updates also require matching `CI_DOTNET_SDK` values in
`.github/workflows/dotnet.yml`, `.github/workflows/release.yml` and
`.github/workflows/stratum-portability.yml`. Dependabot does not edit those variables, so SDK-image
PRs will fail the coordination gate until a maintainer updates all three; review the private SDK
minimum separately. Digests prevent retargeting after review, but do not establish
that the original image is trustworthy. Keep actual-runtime validation and provenance checks.
The PKCS-specific CVE floor remains 10.0.13; it does not replace the newer servicing
requirement. On 9 November 2026, the Ubuntu 24.04 and 26.04 source-build CI logs and
the Ubuntu 26.04 lab confirmed Canonical Core and ASP.NET packages at 10.0.14. That is validation
of those snapshots, not a promise about every mirror or future servicing update. Check the actual
installation and configured package source before rollout. Those 10.0.14 snapshots no longer
meet the current floor:

```console
apt-cache policy dotnet-runtime-10.0 aspnetcore-runtime-10.0
/usr/bin/dotnet --list-runtimes
```

If Canonical's candidate is below the required floor, defer the Miningcore upgrade until it catches
up, or install Microsoft's supported ASP.NET Core runtime archive into a separate, root-owned
versioned directory, following the [official manual installation instructions][manual-dotnet].
Verify the archive's SHA-512 against Microsoft's release metadata and install the native runtime
prerequisites from the existing Ubuntu guide. The ASP.NET runtime archive includes Core. Do not mix
Microsoft and Canonical .NET APT feeds or overwrite `/usr/lib/dotnet`. Maintain and patch a private
installation explicitly; APT will not update it.

For source builds, the Ubuntu 24.04/26.04 helpers also check both framework inventories before
restore, so an old Canonical SDK fails with this guidance instead of an unavailable app-host package.
If building during a distribution servicing gap, install and verify the official **SDK** archive in
a separate directory (the runtime archive alone cannot build). Select it explicitly, for example:

```bash
export DOTNET_ROOT=/opt/dotnet-miningcore-sdk
export DOTNET_ROOT_X64="$DOTNET_ROOT"
export PATH="$DOTNET_ROOT:$PATH"
export MININGCORE_PRIVATE_DOTNET_ROOT="$DOTNET_ROOT"
./build-ubuntu-26.04.sh "$PWD/build/ubuntu-26.04"
```

The private SDK must be stable .NET 10 at or above `MiningcorePrivateSdkMinimum` in the shared
policy (currently 10.0.404), selected in `PATH`, and separate from `/usr/lib/dotnet`. Its resolved
host must not be owned by any dpkg package; a Microsoft APT installation under `/usr/share/dotnet`
is not a private archive installation. Package-query errors fail closed. Both frameworks must also
meet the runtime floor. Canonical's separately maintained SDK feature band remains supported by
the default route's package-provenance and framework checks; its version is not compared with
Microsoft's `10.0.4xx` feature band. The helpers install the current Canonical SDK candidate first.
This explicit selection does not authenticate
the archive; verify Microsoft's SHA-512 first. Native packages still come from Canonical, and no
Microsoft APT feed is added. On 11 November 2026, the source-lane servicing checks confirmed
Canonical SDK 10.0.115 with Core/ASP.NET 10.0.15 on both Ubuntu 24.04 and 26.04.
Both lanes now use the Canonical host and packages, available in `noble-security`
and `resolute-security`, respectively, with the same package checks, runtime floors
and published binding smoke. Prepending `/usr/bin`
also selects Ubuntu's APT CMake/native toolchain ahead of runner-local tools. A later step verifies
that ordering and the resolved SDK and native tool paths. A separate apphost launch
clears `DOTNET_ROOT` and `DOTNET_ROOT_X64` and checks Ubuntu's registered installation files,
including the architecture-specific override when present. This exercises the operator's unaided
`./Miningcore` launch in addition to the pinned-runtime checks. See Microsoft's
[runtime discovery guidance][runtime-discovery].

The package checks verify path, dpkg ownership and Ubuntu maintainer metadata; the maintainer
field alone is not proof of Canonical origin. Use trusted, authenticated Ubuntu APT sources and
inspect package candidates and configured repositories before installation. A candidate shown by
`apt-cache policy` is not proof that an already installed binary came from that repository.
Stronger source assurance for the host, SDK and both runtime packages is tracked in
[issue #290](https://github.com/NINJAK1DD/miningcore/issues/290).

Private-route helper regressions remain covered on ordinary CI runs. For an end-to-end private
build, dispatch `.NET` (`dotnet.yml`) with `private_source_builds=true` on the intended branch.
The optional Ubuntu 24.04/26.04 jobs install the reviewed Microsoft SDK in a separate directory,
select its host and both root variables, then run the source build, published runtime/native checks
and native known-answer tests. The private root is first in `PATH`, immediately followed by
`/usr/bin` so both routes use Ubuntu's APT native toolchain. A later step verifies the persisted
ordering and resolved `dotnet`, `cmake` and `c++` paths. These jobs do not replace either required
Canonical source lane.
They deliberately omit the catch-up sentinel: Canonical availability is expected in this exercise.

If a future servicing gap requires temporarily switching the default source lanes to a private SDK,
make the following coordinated change in one PR:

1. Use the optional private job's pinned `setup-dotnet` action, isolated install directory and build
   step as the template for both required source jobs. Preserve their check names and target matrix.
2. Remove the job's `/usr/lib/dotnet` root overrides. Keep `/usr/bin` immediately after the private
   root with `export PATH="$DOTNET_ROOT:/usr/bin:$PATH"`. Persist that ordering by writing
   `/usr/bin` then the private root to `GITHUB_PATH`, as in the optional job, and retain its later
   selection check.
   Do not clear `MININGCORE_PRIVATE_DOTNET_ROOT`; export it from the selected private root.
   Set and persist `DOTNET_ROOT_X64` to that same root for subsequent validation steps.
3. Retain published runtime/native validation and native vectors. Drop the unaided Canonical
   apphost check from the temporary private lane and restore it on catch-up: during a servicing gap
   it would test the lagging Canonical runtime, not certify the private installation.
4. Enable `check-canonical-servicing.sh` in each temporary default lane. Only its specific
   under-serviced-framework refusal passes; unexpected failures or Canonical catch-up fail CI.
5. Update the workflow-policy tests to enforce that temporary selection, sentinel and mandatory
   smokes, instead of their current prohibition on private setup in the Canonical lanes. Run the
   helper regressions and both real source builds before adopting the fallback.

Restore the complete Canonical selection and unaided launch check when the sentinel reports
catch-up. Do not suppress that failure or lower the runtime floor.

[runtime-discovery]:
  https://learn.microsoft.com/en-us/dotnet/core/runtime-discovery/troubleshoot-app-launch
[dependabot-docker-names]: https://github.com/dependabot/dependabot-core/issues/16574
[dependabot-docker-rebase]: https://github.com/dependabot/dependabot-core/issues/14780

An installed-runtime inventory is only a build preflight. Both source lanes still run the published
dependency smoke and normal startup guard against the actual selected host, as do the archive,
Windows and container checks. Never replace these with a successful inventory check. Use the
selected host for all subsequent validation and service activation.

For example, after installing and validating the selected release at `/opt/dotnet-miningcore`,
use `sudo systemctl edit miningcore` to select that host explicitly (retain your actual application
and configuration paths):

```ini
[Service]
ExecStart=
ExecStart=/opt/dotnet-miningcore/dotnet /opt/miningcore/Miningcore.dll -c /etc/miningcore/config.json
Environment=DOTNET_ROOT=/opt/dotnet-miningcore
```

Schedule a maintenance window. Confirm the release comes from the approved commit and verify its
published checksums. Before stopping the service, preserve its complete tested release,
unit/drop-ins and configuration, and rehearse rollback with a supported patched runtime. Run the
published dependency smoke using the intended host in `PATH`; inspect `systemctl cat miningcore`
and the service account's effective `DOTNET_ROOT`, `DOTNET_ROOT_X64` and roll-forward settings
privately (environment files may contain secrets). Shell runtime inventories alone do not prove
service selection. Stop Miningcore cleanly before replacing files, reload the unit with
`sudo systemctl daemon-reload`, then start it and verify successful startup under that host,
Stratum listeners, daemon connections and payment-processing health.
If acceptance fails, restore the tested application and unit configuration; do not lower the
runtime floor or return to an affected runtime to force startup. These checks supplement the
complete NuGet audit; they do not replace it.

[manual-dotnet]: https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual

### Read-only incident diagnostics on an older host

`--verify-share-recovery-state` may run on an older .NET 10 runtime with a warning. It only inspects
incident evidence; it neither starts services nor acknowledges/clears the fatal latch. The shipped
runtimeconfig still rejects old frameworks before managed code runs. Prefer installing the serviced
runtime. When incident triage must precede that maintenance, make a **separate temporary copy** of
`Miningcore.runtimeconfig.json`, set both framework versions in that copy to the installed .NET 10
patch shown by `dotnet --list-runtimes`, and invoke only the read-only command:

```console
dotnet exec --runtimeconfig /tmp/miningcore-offline.runtimeconfig.json \
  /opt/miningcore/Miningcore.dll --verify-share-recovery-state -c /etc/miningcore/config.json
```

Use the same state-directory setting and file access as the service, preserve the command's exit
status and all incident files, then remove the temporary runtimeconfig. Do not edit the shipped
file or the service command. Normal startup and incident acknowledgement remain subject to the
runtime guard even if an alternate runtimeconfig is supplied. Configuration-dump diagnostics also
remain offline; this procedure does not declare the older runtime safe for service operation.
Keep this exception read-only: future commands that modify evidence, acknowledge incidents,
submit payments or start services must remain inside the enforced runtime boundary.

Baseline source: [Microsoft .NET 10.0.15 servicing release][november-servicing], published
10 November 2026. Assess package-specific advisories as well as the shared frameworks: a runtime
update cannot fix an affected `System.Formats.Cbor` NuGet dependency (CVE-2026-118747).
Check every restored project and the published dependency manifest; update CBOR separately if
present, and retain the complete transitive vulnerable-package audit as release evidence.
The 10 November 2026 restore with SDK 10.0.404 found no `System.Formats.Cbor` in the resolved
graphs of Miningcore, Miningcore.Tests or Miningcore.Tests.ProcessHost; their complete transitive
NuGet audits reported no vulnerable packages. Recheck this evidence whenever dependencies change.

[november-servicing]: https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md
