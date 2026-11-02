#!/usr/bin/env bash
# Build the opt-in Pearl proof worker from the reviewed consensus source.
set -euo pipefail
repo_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
build_root=${1:-"$repo_root/build/pearl-native"}
case "$(uname -s):$(uname -m)" in
  Linux:x86_64) ;;
  *) echo "Pearl worker currently supports Linux x64 only." >&2; exit 1 ;;
esac
getconf GNU_LIBC_VERSION >/dev/null
# Pin rustup's per-directory selection too: upstream's toolchain file says stable.
export RUSTUP_TOOLCHAIN=1.93.1
[[ $(rustc --version | cut -d' ' -f2) == 1.93.1 ]] || {
  echo "Install the reviewed Rust 1.93.1 toolchain before building Pearl." >&2
  exit 1
}
pin=d7c517d267f10f5bcbbdc366f3e603942f9a5ab5
mkdir -p -- "$build_root"
build_root=$(realpath -- "$build_root")
upstream="$build_root/upstream"
helper="$build_root/helper"
if [[ ! -e "$upstream" ]]; then
  git clone --filter=blob:none --no-checkout https://github.com/pearl-research-labs/pearl.git "$upstream"
  git -C "$upstream" checkout --detach "$pin"
fi
[[ $(git -C "$upstream" rev-parse HEAD) == "$pin" ]] || {
  echo "Pearl source cache is at the wrong revision; use a fresh build directory." >&2
  exit 1
}
git -C "$upstream" diff --quiet
git -C "$upstream" diff --cached --quiet
mkdir -p "$helper/src" "$build_root/output"
cp "$repo_root/src/Native/pearl-proof-worker/Cargo.toml" "$helper/"
cp "$repo_root/src/Native/pearl-proof-worker/Cargo.lock" "$helper/"
cp "$repo_root/src/Native/pearl-proof-worker/src/main.rs" "$helper/src/"
(
  cd "$upstream/zk-pow"
  cargo run --locked --release --no-default-features --bin build_cache -- src/circuit/v2_cache.bin src/v1/v1_cache.bin
)
cargo build --locked --release --manifest-path "$helper/Cargo.toml"
worker="$helper/target/release/miningcore-pearl-worker"
expected="miningcore-pearl-worker 2 $pin linux-x64"
[[ $("$worker" --version) == "$expected" ]]
install -m 0755 "$worker" "$build_root/output/miningcore-pearl-worker"
cp "$upstream/LICENSE" "$build_root/output/pearl-upstream-LICENSE"
cargo metadata --locked --format-version 1 --manifest-path "$helper/Cargo.toml" |
  python3 "$repo_root/scripts/pearl/collect-licenses.py" "$build_root/output/licenses"
cp "$helper/Cargo.lock" "$build_root/output/Cargo.lock"
(
  cd "$build_root"
  sha256sum upstream/zk-pow/src/circuit/v2_cache.bin upstream/zk-pow/src/v1/v1_cache.bin > output/CACHE-SHA256SUMS
  rustc --version > output/toolchain.txt
  cargo --version >> output/toolchain.txt
  printf '%s\n' "$pin" > output/upstream-commit.txt
)
(cd "$build_root/output" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)
printf 'Built %s\n' "$build_root/output/miningcore-pearl-worker"
