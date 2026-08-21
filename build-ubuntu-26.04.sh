#!/bin/bash

set -euo pipefail

# Install .NET 10 from Ubuntu's Canonical-maintained package feed. Do not add
# Microsoft's package feed on Ubuntu 26.04 or mix the two package sources.

# install dev-dependencies
sudo apt-get -o Acquire::Retries=3 update
sudo apt-get -o Acquire::Retries=3 -y install \
  dotnet-sdk-10.0 \
  git \
  cmake \
  ninja-build \
  build-essential \
  libssl-dev \
  pkg-config \
  libboost-all-dev \
  libsodium-dev \
  libzmq5 \
  libzmq3-dev \
  libgmp-dev \
  file

dotnet_path=$(readlink -f "$(command -v dotnet)")

if [[ "$dotnet_path" != /usr/lib/dotnet/dotnet ]]; then
  echo "Expected Canonical's /usr/lib/dotnet host, but dotnet resolved to $dotnet_path" >&2
  exit 1
fi

if ! dpkg-query -S "$dotnet_path" | \
    grep -Eq '^dotnet-host(-[0-9.]+)?(:[^:]+)?:'; then
  echo "The resolved dotnet host is not owned by an Ubuntu dotnet-host package" >&2
  exit 1
fi

if ! dpkg-query -W -f='${Status}\n' dotnet-sdk-10.0 | grep -Fxq 'install ok installed'; then
  echo "Ubuntu package dotnet-sdk-10.0 is not installed" >&2
  exit 1
fi

if ! dotnet --list-sdks | grep -Eq '^10\.[^ ]+ \[/usr/lib/dotnet/sdk\]$'; then
  echo "A Canonical-layout .NET 10 SDK was not found under /usr/lib/dotnet/sdk" >&2
  dotnet --list-sdks >&2
  exit 1
fi

dotnet --info

(
  cd src/Miningcore
  BUILDIR=${1:-../../build}
  echo "Building into $BUILDIR"
  source ../../scripts/release/source-build-identity.sh
  BUILD_IDENTITY_ARGS=()
  miningcore_resolve_source_build_identity ../.. BUILD_IDENTITY_ARGS
  dotnet publish -c Release --framework net10.0 -o "$BUILDIR" \
    "${BUILD_IDENTITY_ARGS[@]}"
)
