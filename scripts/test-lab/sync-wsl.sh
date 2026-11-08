#!/usr/bin/env bash
set -euo pipefail
# Windows and Linux must not share bin/obj/native outputs.
if [[ $# != 2 ]]; then
  echo 'Usage: bash sync-wsl.sh /mnt/c/path/to/miningcore /home/user/miningcore' >&2
  exit 1
fi
source_root=$(realpath -- "$1")
destination=$(realpath -m -- "$2")
test -f "$source_root/src/Miningcore/Miningcore.csproj"
case "$destination" in
  /home/*/miningcore) ;;
  *) echo 'Destination must be /home/USER/miningcore.' >&2; exit 1 ;;
esac
test "$source_root" != "$destination"
mkdir -p "$destination"
# Keep independent Linux Git metadata for GitVersion; never share the Windows index.
if ! test -d "$destination/.git"; then
  git clone --no-checkout --no-hardlinks "$source_root" "$destination/.metadata"
  mv "$destination/.metadata/.git" "$destination/.git"
  rmdir "$destination/.metadata"
fi
source_branch=$(git -C "$source_root" symbolic-ref --short HEAD)
git -C "$destination" fetch "$source_root" "+refs/heads/$source_branch:refs/heads/$source_branch" --update-head-ok --tags
git -C "$destination" symbolic-ref HEAD "refs/heads/$source_branch"
git -C "$destination" read-tree HEAD
rsync -a --delete --exclude='.git' --exclude='.worktrees' --exclude='.agents' \
  --exclude='.codex' --exclude='.vs' --exclude='build' --exclude='bin' \
  --exclude='obj' --exclude='*.so' --exclude='*.o' --exclude='TestResults' \
  "$source_root/" "$destination/"
# Normalize executable scripts and patches from Windows working copies.
find "$destination" -type f \( -name '*.sh' -o -name '*.patch' -o -name '*.sha256' \) \
  -not -path '*/bin/*' -not -path '*/obj/*' \
  -exec sed -i 's/\r$//' {} +
printf 'Synced working source to %s\n' "$destination"
