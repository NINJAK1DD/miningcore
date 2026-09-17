# Dependency security

Miningcore restores the complete transitive package graph with `NuGetAuditMode=all`. High and critical
findings (`NU1903` and `NU1904`) fail the build. Low and moderate findings (`NU1901` and `NU1902`) remain
visible as warnings so a newly published advisory cannot unexpectedly prevent every local build and
release while it is being assessed.

Run the same audit explicitly with:

```console
dotnet list src/Miningcore.sln package --vulnerable --include-transitive
```

Upgrade or replace an affected dependency whenever possible. If an immediate upgrade is not possible,
record the affected code path, deployment exposure, compensating controls, owner and removal condition
in the pull request or a tracking issue. A narrowly scoped `NuGetAuditSuppress` for the advisory URL may
be used only with that written risk acceptance; do not disable NuGet auditing or globally suppress the
`NU190x` warning family.

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
