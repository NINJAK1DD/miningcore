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

bash scripts/release/verify-ubuntu-dotnet-sdk.sh

(
  cd src/Miningcore
  BUILDIR=${1:-../../build}
  echo "Building into $BUILDIR"
  source ../../scripts/release/source-build-identity.sh
  BUILD_IDENTITY_ARGS=()
  miningcore_resolve_source_build_identity ../.. BUILD_IDENTITY_ARGS
  BUILD_LOG=$(mktemp)
  trap 'rm -f -- "$BUILD_LOG"' EXIT
  dotnet publish -c Release --framework net10.0 -o "$BUILDIR" \
    "${BUILD_IDENTITY_ARGS[@]}" 2>&1 | tee "$BUILD_LOG"
  bash ../../scripts/release/assert-warning-free-build.sh "$BUILD_LOG"
)
