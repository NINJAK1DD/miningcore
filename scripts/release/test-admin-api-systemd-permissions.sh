#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
security_guide="$repo_root/docs/admin-api-security.md"

if grep -Eq 'install -d.*-g root.*/etc/miningcore' "$security_guide"; then
  echo "The security guide must not change /etc/miningcore to root:root mode 0750" >&2
  exit 1
fi

grep -Fq 'sudo mkdir -p /etc/miningcore' "$security_guide"
grep -Fq 'sudo chown root:miningcore /etc/miningcore' "$security_guide"
grep -Fq 'sudo chmod 0750 /etc/miningcore' "$security_guide"
grep -Fq 'sudo chown root:root /etc/miningcore/miningcore.env' "$security_guide"
grep -Fq 'sudo chmod 0600 /etc/miningcore/miningcore.env' "$security_guide"

if [[ "$(id -u)" -eq 0 ]]; then
  as_root() { "$@"; }
  as_service_user() { runuser -u "$test_user" -- "$@"; }
elif command -v sudo >/dev/null 2>&1; then
  as_root() { sudo "$@"; }
  as_service_user() { sudo -u "$test_user" -- "$@"; }
else
  echo "This permission check requires root or sudo" >&2
  exit 1
fi

test_root="$(mktemp -d)"
test_group="mc-perm-g-$$"
test_user="mc-perm-u-$$"
group_created=false
user_created=false

cleanup() {
  if [[ "$user_created" == true ]]; then
    as_root userdel "$test_user" >/dev/null 2>&1 || true
  fi
  if [[ "$group_created" == true ]]; then
    as_root groupdel "$test_group" >/dev/null 2>&1 || true
  fi
  as_root rm -rf -- "$test_root"
}
trap cleanup EXIT

as_root groupadd --system "$test_group"
group_created=true
as_root useradd --system --no-create-home \
  --home-dir /nonexistent --shell /usr/sbin/nologin \
  --gid "$test_group" "$test_user"
user_created=true

as_root chown root:root "$test_root"
as_root chmod 0755 "$test_root"
as_root install -d -m 0750 -o root -g "$test_group" \
  "$test_root/etc/miningcore"
as_root install -m 0640 -o root -g "$test_group" /dev/null \
  "$test_root/etc/miningcore/config.json"
as_root install -m 0600 -o root -g root /dev/null \
  "$test_root/etc/miningcore/miningcore.env"

as_service_user test -r \
  "$test_root/etc/miningcore/config.json"

if as_service_user test -r \
  "$test_root/etc/miningcore/miningcore.env"; then
  echo "The service account unexpectedly read the root-only admin credential" >&2
  exit 1
fi

echo "Administrative API systemd permissions are safe"
