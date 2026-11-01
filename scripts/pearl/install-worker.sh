#!/usr/bin/env bash
# Install only a complete source-built, checksum-verified worker bundle.
set -euo pipefail
bundle=$(realpath "${1:?usage: install-worker.sh BUNDLE PUBLISH_DIRECTORY}")
publish=$(realpath "${2:?publish directory required}")
case "$(uname -s):$(uname -m)" in Linux:x86_64) ;; *) echo 'Pearl requires Linux glibc x64' >&2; exit 1;; esac
getconf GNU_LIBC_VERSION >/dev/null
expected='miningcore-pearl-worker 2 d7c517d267f10f5bcbbdc366f3e603942f9a5ab5 linux-x64'
[[ -f "$bundle/licenses/cargo-inventory.json" && -f "$bundle/pearl-upstream-LICENSE" ]]
(cd "$bundle" && sha256sum --check --quiet SHA256SUMS)
[[ $("$bundle/miningcore-pearl-worker" --version) == "$expected" ]]
mkdir -p "$publish/pearl"
cp -a "$bundle/." "$publish/pearl/"
chmod 0755 "$publish/pearl/miningcore-pearl-worker"
[[ $("$publish/pearl/miningcore-pearl-worker" --version) == "$expected" ]]
printf 'Installed pinned Pearl worker in %s/pearl\n' "$publish"
