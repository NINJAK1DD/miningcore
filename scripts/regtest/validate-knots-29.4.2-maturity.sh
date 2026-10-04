#!/usr/bin/env bash
# Released Knots consensus/policy boundary test, isolated from all operator nodes.
set -euo pipefail
: "${MININGCORE_TEST_BLAKE2B_BITCOIND:?Set to the checksum-verified Knots 29.4.2 binary}"
binary="$(realpath "$MININGCORE_TEST_BLAKE2B_BITCOIND")"
"$binary" --version | head -n 1 | grep -F 'v29.4.2.knots20260508'
fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/miningcore-knots-2942.XXXXXXXX")"
trap 'rm -rf -- "$fixture_root"' EXIT
archive="$fixture_root/source.tar.gz"
curl --fail --location --retry 3 --connect-timeout 15 --max-time 180 \
  'https://bitcoinknots.org/files/29.x/29.4.2.knots20260508/bitcoin-29.4.2.knots20260508.tar.gz' -o "$archive"
echo "11c0b99a82b8b1c9c29ab76d9b0507ce1017813665741627b3f3883a4c2f7a7f  $archive" | sha256sum --check --strict
mkdir "$fixture_root/source"
tar -xzf "$archive" --strip-components=1 -C "$fixture_root/source"
test_script="$fixture_root/source/test/functional/mempool_long_coinbase_maturity.py"
# Confirm that the shipped test is identical to the reviewed immutable source.
echo "ecef521550daa248222661bf90f2675dee2be3001aa8643e879f18aefb4e7fe3  $test_script" | sha256sum --check --strict
revalidation_script="$fixture_root/source/test/functional/feature_chainstate_revalidation.py"
echo "73c55c47ea4f07e71cf6330c392f08e118845df81a612c78c1b80ee3041186be  $revalidation_script" | sha256sum --check --strict
cat > "$fixture_root/config.ini" <<EOF
[environment]
CLIENT_NAME=Bitcoin Knots
CLIENT_BUGREPORT=https://github.com/bitcoinknots/bitcoin/issues
SRCDIR=$fixture_root/source
BUILDDIR=$(dirname "$(dirname "$binary")")
EXEEXT=
RPCAUTH=$fixture_root/source/share/rpcauth/rpcauth.py
[components]
ENABLE_WALLET=true
USE_SQLITE=true
USE_BDB=false
ENABLE_CLI=true
ENABLE_BITCOIN_UTIL=true
ENABLE_WALLET_TOOL=true
ENABLE_UTIL_TX=true
ENABLE_UTIL_UTIL=true
ENABLE_BITCOIND=true
ENABLE_FUZZ_BINARY=false
ENABLE_ZMQ=false
ENABLE_EXTERNAL_SIGNER=true
ENABLE_USDT_TRACEPOINTS=false
EOF
BITCOIND="$binary" BITCOINCLI="$(dirname "$binary")/bitcoin-cli" PYTHONDONTWRITEBYTECODE=1 \
  python3 "$test_script" --configfile="$fixture_root/config.ini" \
  --cachedir="$fixture_root/cache" --tmpdir="$fixture_root/run" --portseed=205 --timeout-factor=3
BITCOIND="$binary" BITCOINCLI="$(dirname "$binary")/bitcoin-cli" PYTHONDONTWRITEBYTECODE=1 \
  python3 "$revalidation_script" --configfile="$fixture_root/config.ini" \
  --cachedir="$fixture_root/cache" --tmpdir="$fixture_root/revalidation" --portseed=205 --timeout-factor=3
