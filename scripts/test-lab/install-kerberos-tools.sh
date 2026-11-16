#!/usr/bin/env bash
# Ubuntu lab tools only: do not configure a system realm or run package scripts.
set -euo pipefail
umask 077
if [[ $(id -u) == 0 || $# != 1 ]]; then
  echo 'Usage (ordinary lab user): install-kerberos-tools.sh PRIVATE_ROOT' >&2
  exit 64
fi
mkdir -p "$1"
root=$(cd "$1" && pwd)
if [[ -e $root/tools || -e $root/packages ]]; then
  echo 'Use a fresh private directory; do not mix extracted package versions.' >&2
  exit 64
fi
mkdir -p "$root/packages" "$root/tools"
cd "$root/packages"
# apt verifies downloads against authenticated repository metadata. Keep the .debs
# and their checksums as exact-version evidence; no dpkg maintainer script runs.
apt-get download krb5-kdc krb5-user krb5-admin-server libverto1t64 libverto-libevent1t64 liblmdb0 libevent-core-2.1-7t64
for package in ./*.deb; do
  dpkg-deb -x "$package" "$root/tools"
done
for name in kinit klist; do
  ln -s "$name.mit" "$root/tools/usr/bin/$name"
done
sha256sum ./*.deb > "$root/packages.sha256"
dpkg-deb -W ./*.deb > "$root/packages.txt"
echo "Private Kerberos tools installed under $root/tools"
