#!/usr/bin/env bash

set -euo pipefail

publish_dir=${1:?usage: test-ubuntu-2604-native-vectors.sh PUBLISH_DIRECTORY}
publish_dir=$(realpath "$publish_dir")
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
test_project="$repository_root/src/Miningcore.Tests/Miningcore.Tests.csproj"
test_output="$repository_root/src/Miningcore.Tests/bin/Release/net10.0"

# Build only the managed test host. The reviewed GCC 15 libraries have already been built and
# published, and are copied into the test output below so these tests execute those exact files.
dotnet build "$test_project" \
  --configuration Release \
  --framework net10.0 \
  -p:MiningcoreStratumListenerSmoke=true

mkdir -p "$test_output"
rm -f "$test_output"/*.so
cp "$publish_dir"/*.so "$test_output/"

filter='FullyQualifiedName~Miningcore.Tests.Crypto.CrytonoteTests'
filter+='|FullyQualifiedName~Miningcore.Tests.Crypto.HashingTests.Yescrypt'
filter+='|FullyQualifiedName~Miningcore.Tests.Crypto.HashingTests.Flex_'
filter+='|FullyQualifiedName~Miningcore.Tests.Crypto.HashingTests.Zanonote_'

LD_LIBRARY_PATH="$publish_dir:$test_output${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  dotnet test "$test_project" \
    --no-build \
    --no-restore \
    --configuration Release \
    --framework net10.0 \
    -p:MiningcoreStratumListenerSmoke=true \
    --filter "$filter"
