#!/usr/bin/env bash
# Keep the default Ubuntu route observable while CI builds with a private SDK.
set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
log=$(mktemp)
trap 'rm -f -- "$log"' EXIT
status=0
env -u MININGCORE_PRIVATE_DOTNET_ROOT -u DOTNET_ROOT -u DOTNET_ROOT_X64 \
  -u DOTNET_HOST_PATH -u DOTNET_ROLL_FORWARD PATH=/usr/bin:/bin \
  bash "$script_dir/verify-ubuntu-dotnet-sdk.sh" > "$log" 2>&1 || status=$?
cat "$log"
if [[ "$status" -eq 0 ]]; then
  echo "Canonical caught up — switch the source-build lane back to Canonical and remove this sentinel invocation." >&2
  exit 1
fi
if [[ "$status" -eq 78 ]] && grep -Fq 'Wait for serviced Canonical packages' "$log"; then
  echo "Canonical provenance verified; servicing still lags. Keep the explicit private SDK and actual-runtime smoke."
  exit 0
fi
echo "Canonical preflight failed unexpectedly (exit $status); do not classify this as servicing lag." >&2
exit 1
