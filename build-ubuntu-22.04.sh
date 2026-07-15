#!/bin/bash

# Install .NET 10 from Canonical's Ubuntu 22.04 backports feed.

# install install-dependencies
sudo apt-get update && \
  sudo apt-get -y install software-properties-common

# add dotnet repo
sudo add-apt-repository -y ppa:dotnet/backports

# install dev-dependencies
sudo apt-get update && \
  sudo apt-get -y install dotnet-sdk-10.0 git cmake ninja-build build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5 libzmq3-dev libgmp-dev

(cd src/Miningcore && \
BUILDIR=${1:-../../build} && \
echo "Building into $BUILDIR" && \
dotnet publish -c Release --framework net10.0 -o $BUILDIR)
