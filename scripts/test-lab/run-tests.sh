#!/usr/bin/env bash
set -euo pipefail
if [[ $(id -u) == 0 ]]; then
  echo 'Run as an ordinary user: PostgreSQL TLS fixtures cannot run as root.' >&2
  exit 1
fi
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$repo"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_UI_LANGUAGE=en LC_ALL=C
export MININGCORE_TEST_BITCOIND=/opt/miningcore-test-lab/bin/bitcoind
export MININGCORE_TEST_LITECOIND=/opt/miningcore-test-lab/bin/litecoind
export MININGCORE_TEST_DOGECOIND=/opt/miningcore-test-lab/bin/dogecoind
export MININGCORE_TEST_POSTGRES="${MININGCORE_TEST_POSTGRES:-Host=127.0.0.1;Port=55432;Username=miningcore;Password=miningcore-test;Database=miningcore_tests}"
pg_bins=(/usr/lib/postgresql/*/bin/initdb)
export MININGCORE_TEST_POSTGRES_BIN="${MININGCORE_TEST_POSTGRES_BIN:-$(dirname "${pg_bins[-1]}")}"
for binary in "$MININGCORE_TEST_BITCOIND" "$MININGCORE_TEST_LITECOIND" \
  "$MININGCORE_TEST_DOGECOIND" "$MININGCORE_TEST_POSTGRES_BIN/initdb"; do
  test -x "$binary" || { echo "Missing test dependency: $binary" >&2; exit 1; }
done
bash scripts/release/verify-ubuntu-dotnet-sdk.sh
dotnet restore src
mkdir -p build/test-lab
: > build/test-lab/build.log
bash scripts/release/run-warning-audited-dotnet.sh build/test-lab/build.log \
  build src --no-restore --no-incremental
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --logger 'trx;LogFileName=ubuntu-test-lab.trx' --results-directory build/test-lab "$@"
