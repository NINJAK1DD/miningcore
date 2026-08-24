#!/bin/bash

set -euo pipefail

# install install-dependencies
sudo apt-get -o Acquire::Retries=3 update
sudo apt-get -o Acquire::Retries=3 -y install wget

# add dotnet repo
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# install dev-dependencies
sudo apt-get -o Acquire::Retries=3 update
sudo apt-get -o Acquire::Retries=3 -y install \
  dotnet-sdk-10.0 \
  git \
  cmake \
  clang \
  ninja-build \
  build-essential \
  libssl-dev \
  pkg-config \
  libboost-all-dev \
  libsodium-dev \
  libzmq5-dev \
  libgmp-dev \
  libc++-dev \
  zlib1g-dev

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
