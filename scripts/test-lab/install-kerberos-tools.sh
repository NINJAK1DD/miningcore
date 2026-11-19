#!/usr/bin/env bash
# Ubuntu lab tools only: do not configure a system realm or run package scripts.
set -euo pipefail
umask 077
if [[ $(id -u) == 0 || $# != 1 ]]; then
  echo 'Usage (ordinary lab user): install-kerberos-tools.sh PRIVATE_ROOT' >&2
  exit 64
fi
manifest=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/kerberos-packages.sha256
mkdir -p "$1"
root=$(cd "$1" && pwd)
if [[ -e $root/tools || -e $root/packages ]]; then
  echo 'Use a fresh private directory; do not mix extracted package versions.' >&2
  exit 64
fi
mkdir -p "$root/packages" "$root/tools"
cd "$root/packages"
# Pin the qualified Ubuntu 26.04 amd64 artifacts. If a repository retires a
# version, fail rather than silently qualifying different tools. Review new pins
# and hashes together and rerun the live matrix before adopting an update.
apt-get download \
  krb5-kdc:amd64=1.22.1-2ubuntu4.1 \
  krb5-user:amd64=1.22.1-2ubuntu4.1 \
  krb5-admin-server:amd64=1.22.1-2ubuntu4.1 \
  libverto1t64:amd64=0.3.1-1.2ubuntu4 \
  libverto-libevent1t64:amd64=0.3.1-1.2ubuntu4 \
  liblmdb0:amd64=0.9.31-1build2 \
  libevent-core-2.1-7t64:amd64=2.1.12-stable-10ubuntu0.2
sha256sum --strict --check "$manifest"
# Verify every artifact before extraction; no dpkg maintainer script runs.
: > "$root/packages.txt"
for package in ./*.deb; do
  dpkg-deb -x "$package" "$root/tools"
  dpkg-deb -W "$package" >> "$root/packages.txt"
done
for name in kinit klist; do
  ln -s "$name.mit" "$root/tools/usr/bin/$name"
done
sha256sum ./*.deb > "$root/packages.sha256"
echo "Private Kerberos tools installed under $root/tools"
