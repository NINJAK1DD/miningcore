#!/usr/bin/env bash

set -euo pipefail

dotnet_path=$(readlink -f "$(command -v dotnet)")

if [[ -n "${MININGCORE_PRIVATE_DOTNET_ROOT:-}" ]]; then
  private_host=$(readlink -f "$MININGCORE_PRIVATE_DOTNET_ROOT/dotnet")
  if [[ ! -x "$private_host" || "$private_host" == /usr/lib/dotnet/dotnet || "$dotnet_path" != "$private_host" ]]; then
    echo "The explicit private SDK must be separate from /usr/lib/dotnet and selected in PATH" >&2
    exit 1
  fi
  echo "Using explicitly selected private SDK: $private_host"
  echo "Verify its archive provenance and service this installation separately from APT."
  if [[ ! "$(dotnet --version)" =~ ^10[.]0[.][0-9]+$ ]]; then
    echo "The private installation must contain a stable .NET 10 SDK" >&2
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
policy="$(dirname "${BASH_SOURCE[0]}")/../../src/Miningcore/RuntimeDependencyPolicy.props"
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
    exit 1
  fi
done

dotnet --info
