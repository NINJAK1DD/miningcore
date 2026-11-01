#!/usr/bin/env bash
# Install only a complete source-built, checksum-verified worker bundle.
set -euo pipefail
bundle=$(realpath "${1:?usage: install-worker.sh BUNDLE PUBLISH_DIRECTORY}")
publish=$(realpath "${2:?publish directory required}")
case "$(uname -s):$(uname -m)" in Linux:x86_64) ;; *) echo 'Pearl requires Linux glibc x64' >&2; exit 1;; esac
getconf GNU_LIBC_VERSION >/dev/null
expected='miningcore-pearl-worker 2 d7c517d267f10f5bcbbdc366f3e603942f9a5ab5 linux-x64'
[[ -f "$bundle/licenses/cargo-inventory.json" && -f "$bundle/pearl-upstream-LICENSE" ]]
[[ -z $(find "$bundle" -type l -print -quit) ]] || { echo 'Worker bundles must not contain symbolic links' >&2; exit 1; }
(cd "$bundle" && sha256sum --check --quiet SHA256SUMS)
[[ $("$bundle/miningcore-pearl-worker" --version) == "$expected" ]]
[[ ! -L "$publish/pearl" ]] || { echo 'Worker destination must not be a symbolic link' >&2; exit 1; }
if [[ -d "$publish/pearl" ]]; then
  [[ -z $(find "$publish/pearl" -type l -print -quit) ]] || { echo 'Existing worker destination contains symbolic links' >&2; exit 1; }
fi
mkdir -p "$publish/pearl"
cp -a --no-preserve=ownership --remove-destination "$bundle/." "$publish/pearl/"
find "$publish/pearl" -type d -exec chmod 0755 {} +
find "$publish/pearl" -type f -exec chmod 0644 {} +
chmod 0555 "$publish/pearl/miningcore-pearl-worker"
[[ $("$publish/pearl/miningcore-pearl-worker" --version) == "$expected" ]]
printf 'Installed pinned Pearl worker in %s/pearl\n' "$publish"
