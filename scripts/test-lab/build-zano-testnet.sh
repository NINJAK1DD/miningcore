#!/usr/bin/env bash
# Build the upstream testnet variant: the mainnet release refuses fresh-chain
# spending below its checkpoints. No wallet or consensus source is changed.
set -euo pipefail
umask 077
[[ $(id -u) != 0 ]] || { echo 'Run as an ordinary lab user.' >&2; exit 1; }
root=$(realpath -e -- "${1:?Usage: build-zano-testnet.sh WALLET_INSTALL_ROOT}")
for tool in gcc-12 g++-12 cmake git curl python3; do
  command -v "$tool" >/dev/null || { echo "Missing dependency: $tool" >&2; exit 1; }
done
cd "$root"
archive=boost_1_83_0.tar.bz2
if [[ ! -f $archive ]]; then
  curl --fail --location --retry 3 --output "$archive.part" \
    https://archives.boost.io/release/1.83.0/source/boost_1_83_0.tar.bz2
  mv -- "$archive.part" "$archive"
fi
echo '6478edfe2f3305127cffe8caf73ea0176c53769f4bf1585be237eb30798c3b8e  boost_1_83_0.tar.bz2' | sha256sum --check --strict -
if [[ ! -d boost_1_83_0 ]]; then
  python3 -c 'import tarfile; t=tarfile.open("boost_1_83_0.tar.bz2"); t.extractall(".", filter="data")'
fi
(
  cd boost_1_83_0
  ./bootstrap.sh --with-libraries=system,filesystem,locale,thread,timer,date_time,chrono,regex,serialization,atomic,program_options,log
  ./b2 -j"${JOBS:-4}" toolset=gcc-12 link=static variant=release stage
)
if [[ ! -d zano-testnet-source ]]; then
  git clone --depth 1 --branch 2.2.3.608 --recursive \
    https://github.com/hyle-team/zano.git zano-testnet-source
fi
cd zano-testnet-source
[[ $(git rev-parse HEAD) == 09c052615e8327aca7d8c849f80d97f3342cc3d4 ]] || {
  echo 'Unexpected Zano source revision' >&2; exit 1;
}
git submodule update --init --recursive
# Ubuntu 26.04 supplies BFD rather than the obsolete gold linker. Refuse to
# overwrite unrelated local edits; the adaptation affects linking only.
python3 - <<'PY'
from pathlib import Path
import subprocess
path = Path('CMakeLists.txt')
original = subprocess.check_output(['git', 'show', 'HEAD:CMakeLists.txt']).decode()
adapted = original.replace('-fuse-ld=gold', '-fuse-ld=bfd')
if path.read_text() not in (original, adapted):
    raise SystemExit('Unexpected local CMake changes; preserve and review them first')
changed = subprocess.check_output(['git', 'diff', 'HEAD', '--name-only']).decode().splitlines()
if any(name != 'CMakeLists.txt' for name in changed):
    raise SystemExit('Unexpected local source changes')
path.write_text(adapted)
PY
cmake -S . -B build-testnet-gcc12 -DTESTNET=ON -DBUILD_GUI=OFF -DBUILD_TESTS=OFF \
  -DBOOST_ROOT="$root/boost_1_83_0" -DBoost_NO_BOOST_CMAKE=ON \
  -DCMAKE_C_COMPILER=gcc-12 -DCMAKE_CXX_COMPILER=g++-12 -DCMAKE_BUILD_TYPE=Release
cmake --build build-testnet-gcc12 --target daemon simplewallet -j"${JOBS:-4}"
sha256sum build-testnet-gcc12/src/zanod build-testnet-gcc12/src/simplewallet
