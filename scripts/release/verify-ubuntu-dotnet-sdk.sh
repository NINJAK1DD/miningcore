#!/usr/bin/env bash

set -euo pipefail

dotnet_path=$(readlink -f "$(command -v dotnet)")
policy="$(dirname "${BASH_SOURCE[0]}")/../../src/Miningcore/RuntimeDependencyPolicy.props"

if [[ -n "${MININGCORE_PRIVATE_DOTNET_ROOT:-}" ]]; then
  private_host=$(readlink -f "$MININGCORE_PRIVATE_DOTNET_ROOT/dotnet")
  if [[ ! -x "$private_host" || "$private_host" == /usr/lib/dotnet/dotnet || "$dotnet_path" != "$private_host" ]]; then
    echo "The explicit private SDK must be separate from /usr/lib/dotnet and selected in PATH" >&2
    exit 1
  fi
  if dpkg-query -S "$private_host" > /dev/null 2>&1; then
    echo "The private SDK host is owned by a dpkg package; use a separate verified archive, not another APT feed" >&2
    exit 1
  else
    package_status=$?
    if [[ "$package_status" -ne 1 ]]; then
      echo "Unable to establish that the private SDK is not package-managed (dpkg-query exit $package_status)" >&2
      exit 1
    fi
  fi
  echo "Using explicitly selected private SDK: $private_host"
  echo "Verify its archive provenance and service this installation separately from APT."
  sdk_minimum=$(sed -n 's/.*<MiningcorePrivateSdkMinimum>\([^<]*\)<\/MiningcorePrivateSdkMinimum>.*/\1/p' "$policy")
  sdk_version=$(dotnet --version)
  if [[ ! "$sdk_minimum" =~ ^10[.]0[.][0-9]+$ || ! "$sdk_version" =~ ^10[.]0[.][0-9]+$ ]]; then
    echo "The private SDK and its reviewed minimum must be stable .NET 10 SDK versions" >&2
    exit 1
  fi
  if ((10#${sdk_version##*.} < 10#${sdk_minimum##*.})); then
    echo "Private SDK $sdk_version is below the reviewed minimum $sdk_minimum; update the separate SDK installation" >&2
    exit 1
  fi
else
  if [[ "$dotnet_path" != /usr/lib/dotnet/dotnet ]]; then
    echo "Expected Canonical's /usr/lib/dotnet host, but dotnet resolved to $dotnet_path" >&2
    exit 1
  fi

  if ! dotnet_host_owner=$(dpkg-query -S "$dotnet_path" 2>&1); then
    echo "The resolved dotnet host is not owned by any dpkg package: $dotnet_host_owner" >&2
    exit 1
  fi

  if [[ ! "$dotnet_host_owner" =~ ^(dotnet-host(-[0-9.]+)?(:[^:]+)?): ]]; then
    echo "The resolved dotnet host is not owned by an Ubuntu dotnet-host package" >&2
    exit 1
  fi

  dotnet_host_package=${BASH_REMATCH[1]}
  dotnet_host_maintainer=$(dpkg-query -W -f='${Maintainer}\n' "$dotnet_host_package")

  if ! grep -Fiq 'ubuntu.com' <<<"$dotnet_host_maintainer"; then
    echo "$dotnet_host_package is not maintained by Ubuntu: $dotnet_host_maintainer" >&2
    exit 1
  fi

  if ! dpkg-query -W -f='${Status}\n' dotnet-sdk-10.0 | grep -Fxq 'install ok installed'; then
    echo "Ubuntu package dotnet-sdk-10.0 is not installed" >&2
    exit 1
  fi

  dotnet_sdk_maintainer=$(dpkg-query -W -f='${Maintainer}\n' dotnet-sdk-10.0)

  if ! grep -Fiq 'ubuntu.com' <<<"$dotnet_sdk_maintainer"; then
    echo "dotnet-sdk-10.0 is not maintained by Ubuntu: $dotnet_sdk_maintainer" >&2
    exit 1
  fi

  if ! dotnet --list-sdks | grep -Eq '^10\.[^ ]+ \[/usr/lib/dotnet/sdk\]$'; then
    echo "A Canonical-layout .NET 10 SDK was not found under /usr/lib/dotnet/sdk" >&2
    dotnet --list-sdks >&2
    exit 1
  fi
fi

# Refuse before restore when Canonical's app-host pack/runtime lags the policy.
# This inventory preflight supplements, rather than replaces, the published binding smoke.
minimum=$(sed -n 's/.*<MiningcoreRuntimeMinimum>\([^<]*\)<\/MiningcoreRuntimeMinimum>.*/\1/p' "$policy")
if [[ ! "$minimum" =~ ^10[.]0[.][0-9]+$ ]]; then
  echo "Unable to read the reviewed runtime minimum from $policy" >&2
  exit 1
fi
runtimes=$(dotnet --list-runtimes)
for framework in Microsoft.NETCore.App Microsoft.AspNetCore.App; do
  if ! awk -v framework="$framework" -v minimum="${minimum##*.}" '
      $1 == framework && $2 ~ /^10[.]0[.][0-9]+$/ {
        split($2, version, "."); if (version[3] >= minimum) found = 1
      }
      END { exit !found }
    ' <<<"$runtimes"; then
    echo "$framework $minimum or later in .NET 10 is required before building." >&2
    echo "Wait for serviced Canonical packages, or select a verified private SDK as documented in docs/dependency-security.md." >&2
    # A distinct status lets the Canonical CI sentinel distinguish servicing lag
    # from provenance, configuration and command failures.
    exit 78
  fi
done

dotnet --info
