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
source of the .NET/ASP.NET servicing floor and PKCS security floor. Publishing uses
`TargetLatestRuntimePatch` and an explicit `RuntimeFrameworkVersion`; the gate checks the emitted
runtimeconfig, not just these build properties. The host refuses older frameworks. Normal
Miningcore startup also checks the selected Core runtime, an actual ASP.NET shared-framework
assembly and PKCS before opening connections, including when runtimeconfig settings were changed.
Configuration-dump diagnostics remain offline and do not start mining services.

The smoke prints both selected framework versions/locations and PKCS's informational version and
location under Miningcore's published manifest/runtimeconfig. Package mode accepts the exact
application copy or an equal-or-newer copy from the selected ASP.NET framework. Framework mode
requires that selected framework's copy. Installed-runtime inventories are additional diagnostics,
not proof of selection. The same checks run against Windows publishing and inside both source and
packaged container images, as their normal unprivileged user. NBitcoin.Zcash binding is also tested.

Run the portable gate on Windows with:

```console
python scripts/release/run-managed-dependency-smoke.py build/windows-x64
```

Before deployment, keep both shared frameworks patched to the latest supported .NET 10 servicing
release, at least the common policy floor, and restart the service. CI validates its own hosts and
built images; verify the production service's installation, `DOTNET_ROOT` and roll-forward settings
as well. These checks supplement the complete NuGet audit; they do not replace it.

Baseline source: [Microsoft .NET 10.0.14 servicing release](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.14/10.0.14.md).
