#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
operations="$repo_root/docs/operations.md"
mainnet="$repo_root/docs/mainnet-validation.md"

assert_contains() {
  local file=$1
  local description=$2
  local text=$3

  if ! grep -Fq "$text" "$file"; then
    echo "$file is missing: $description" >&2
    exit 1
  fi
}

assert_not_contains() {
  local file=$1
  local description=$2
  local text=$3

  if grep -Fq "$text" "$file"; then
    echo "$file still contains: $description" >&2
    exit 1
  fi
}

assert_contains "$operations" "per-generation Bitcoin manifest" \
  'SHA256SUMS.bitcoin-${stamp}'
assert_contains "$operations" "per-generation Litecoin manifest" \
  'SHA256SUMS.litecoin-${stamp}'
assert_contains "$operations" "per-generation Dogecoin manifest" \
  'SHA256SUMS.dogecoin-${stamp}'
assert_contains "$operations" "root-only manifest creation" \
  'umask 077'
assert_contains "$operations" "collision-resistant timestamp" \
  'date -u +%Y%m%dT%H%M%S%NZ'
assert_contains "$operations" "manifest overwrite protection" \
  'set -C'
assert_contains "$operations" "Dogecoin backup-directory coupling" \
  'backupdir=/srv/wallet-backups/dogecoin'
assert_contains "$operations" "hardened systemd write path" \
  'ReadWritePaths=/srv/wallet-backups/REPLACE_WITH_DAEMON'
assert_contains "$operations" "paired retention rule" \
  'paired `SHA256SUMS.*` manifest'
assert_contains "$operations" "destination-side manifest discovery" \
  'find . -maxdepth 1 -type f -name "SHA256SUMS.*"'
assert_contains "$operations" "orphaned backup detection" \
  'Backup has no paired checksum manifest:'
assert_not_contains "$operations" "forever-growing shared manifest append" \
  '>> SHA256SUMS'
while IFS= read -r match; do
  if [[ "$match" != *"SHA-256"* ]]; then
    echo "Documentation contains an unlabeled 64-character identifier: $match" >&2
    exit 1
  fi
done < <(grep -REn '(^|[^0-9A-Fa-f])[0-9A-Fa-f]{64}([^0-9A-Fa-f]|$)' \
  "$repo_root/docs" --include='*.md' || true)
assert_contains "$mainnet" "transaction identifier placeholder" \
  "TXID='REPLACE_WITH_TRANSACTION_ID'"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

source_root="$tmp/source"
external_root="$tmp/external"
mkdir -p "$source_root/bitcoin" "$external_root"

create_generation() {
  local stamp=$1
  local relative="bitcoin/bitcoin-pool-${stamp}.dat"
  local manifest="SHA256SUMS.bitcoin-${stamp}"

  printf 'wallet backup %s\n' "$stamp" > "$source_root/$relative"
  (
    umask 077
    set -C
    cd "$source_root"
    sha256sum -- "$relative" > "$manifest"
  )
}

verify_manifests() {
  local root=$1
  local count=0
  declare -A covered=()

  while IFS= read -r -d '' manifest; do
    if ! (
      cd "$root"
      sha256sum --check -- "$manifest"
    ); then
      return 1
    fi
    mapfile -t entries < <(cut -d ' ' -f 3- -- "$manifest")
    test "${#entries[@]}" -eq 1 || return 1
    backup="./${entries[0]}"
    test -z "${covered[$backup]+present}" || return 1
    covered["$backup"]=1
    count=$((count + 1))
  done < <(find "$root" -maxdepth 1 -type f -name 'SHA256SUMS.*' -print0 | sort -z)

  test "$count" -gt 0 || return 1

  while IFS= read -r -d '' absolute_backup; do
    backup="./${absolute_backup#"$root/"}"
    test -n "${covered[$backup]+present}" || return 1
  done < <(find "$root" -mindepth 2 -maxdepth 2 -type f -name '*.dat' -print0 | sort -z)
}

create_generation 20260809T010000Z
if create_generation 20260809T010000Z >/dev/null 2>&1; then
  echo "Manifest creation unexpectedly overwrote an existing generation" >&2
  exit 1
fi
create_generation 20260809T020000Z
cp -a "$source_root/." "$external_root/"

if grep -R -Fq "$source_root" "$source_root"/SHA256SUMS.*; then
  echo "Backup manifests must contain portable relative paths" >&2
  exit 1
fi

verify_manifests "$external_root" >/dev/null

printf 'uncovered backup\n' > "$external_root/bitcoin/orphan.dat"
if verify_manifests "$external_root" >/dev/null 2>&1; then
  echo "Destination verification unexpectedly accepted an orphaned backup" >&2
  exit 1
fi
rm "$external_root/bitcoin/orphan.dat"

rm "$external_root/bitcoin/bitcoin-pool-20260809T020000Z.dat"
if verify_manifests "$external_root" >/dev/null 2>&1; then
  echo "Destination verification unexpectedly accepted a missing backup" >&2
  exit 1
fi

rm -rf "$external_root"
mkdir -p "$external_root"
cp -a "$source_root/." "$external_root/"
rm "$external_root/bitcoin/bitcoin-pool-20260809T010000Z.dat" \
  "$external_root/SHA256SUMS.bitcoin-20260809T010000Z"
verify_manifests "$external_root" >/dev/null

echo "Wallet-backup documentation invariants and manifest behavior are valid"
