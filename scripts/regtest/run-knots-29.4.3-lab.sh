#!/usr/bin/env bash
# Run in the documented Ubuntu lab as its ordinary user, in a Linux checkout.
set -euo pipefail
[[ $(id -u) != 0 ]] || { echo 'Use an ordinary lab user.' >&2; exit 1; }
: "${MININGCORE_TEST_BLAKE2B_BITCOIND:?Set to verified Knots 29.4.3 bitcoind}"
: "${MININGCORE_TEST_POSTGRES:?Set to the isolated loopback lab database}"
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$repo"
export MININGCORE_TEST_BITCOIND=${MININGCORE_TEST_BITCOIND:-/opt/miningcore-test-lab/bin/bitcoind}
export MININGCORE_TEST_LITECOIND=${MININGCORE_TEST_LITECOIND:-/opt/miningcore-test-lab/bin/litecoind}
export MININGCORE_TEST_DOGECOIND=${MININGCORE_TEST_DOGECOIND:-/opt/miningcore-test-lab/bin/dogecoind}
pg_bins=(/usr/lib/postgresql/*/bin/initdb)
export MININGCORE_TEST_POSTGRES_BIN=${MININGCORE_TEST_POSTGRES_BIN:-$(dirname "${pg_bins[-1]}")}
for binary in "$MININGCORE_TEST_BLAKE2B_BITCOIND" "$MININGCORE_TEST_BITCOIND" \
  "$MININGCORE_TEST_LITECOIND" "$MININGCORE_TEST_DOGECOIND" "$MININGCORE_TEST_POSTGRES_BIN/initdb"; do
  test -x "$binary" || { echo "Missing lab dependency: $binary" >&2; exit 1; }
done
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_UI_LANGUAGE=en LC_ALL=C
mkdir -p build/knots-29.4.3-lab
bash scripts/regtest/validate-knots-29.4.3-maturity.sh > build/knots-29.4.3-lab/upstream.log 2>&1
dotnet restore src
: > build/knots-29.4.3-lab/build.log
bash scripts/release/run-warning-audited-dotnet.sh build/knots-29.4.3-lab/build.log build src --no-restore
dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --logger 'trx;LogFileName=knots-29.4.3-lab.trx' --results-directory build/knots-29.4.3-lab "$@"
