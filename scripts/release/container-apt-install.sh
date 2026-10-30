#!/usr/bin/env bash
set -euo pipefail

# A single wall-time budget includes update, downloads, retries and installation.
# Keep normal APT signature/hash verification; never substitute an unauthenticated
# mirror or continue with a partially installed dependency set.
recommendations=--no-install-recommends
if [[ ${1:-} == --install-recommends ]]; then
  recommendations=--install-recommends
  shift
fi
budget=${MININGCORE_CONTAINER_APT_TIMEOUT_SECONDS:-1800}
if [[ ! $budget =~ ^[0-9]{1,4}$ ]] || (( 10#$budget > 1800 )) || (( $# == 0 )); then
  echo 'Invalid container dependency budget or empty package list' >&2
  exit 64
fi
for package in "$@"; do
  if [[ ! $package =~ ^[a-z0-9][a-z0-9+.-]*$ ]]; then
    echo 'Container dependency arguments must be package names' >&2
    exit 64
  fi
done
budget=$((10#$budget))
printf 'container_dependencies start timeout_seconds=%s\n' "$budget"
# GNU timeout treats zero as disabled. This opt-in is for slow local builds;
# workflow policy tests require every CI caller to use a positive bounded value.
status=0
timeout --signal=TERM --kill-after=10s "${budget}s" bash -euo pipefail -c '
  apt-get -o APT::Update::Error-Mode=any -o Acquire::Retries=2 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 update
  apt-get -o Acquire::Retries=2 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 install -y "$@"
' -- "$recommendations" "$@" || status=$?
printf 'container_dependencies complete status=%s\n' "$status"
if (( status == 124 )); then
  echo 'Container dependency acquisition exceeded its bounded budget' >&2
elif (( status == 137 )); then
  echo 'Container dependency acquisition was killed (deadline escalation or external SIGKILL/OOM)' >&2
fi
exit "$status"
