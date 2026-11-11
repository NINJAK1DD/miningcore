#!/usr/bin/env bash
set -euo pipefail

# The directory argument is a fixture seam for sourced tests only. CLI checks
# always use the host's real registration and published executable.
validate_apphost_discovery() (
  set -euo pipefail
  publish_dir=$(realpath "$1")
  registration_dir=${2:-/etc/dotnet}

  for name in install_location install_location_x64; do
    registration="$registration_dir/$name"
    if [[ "$name" == install_location_x64 && ! -e "$registration" && ! -L "$registration" ]]; then
      continue
    fi
    if [[ ! -f "$registration" ]] || ! location=$(cat "$registration"); then
      echo "Missing or unreadable runtime registration: $registration" >&2
      return 1
    fi
    printf '%s: %s\n' "$name" "$location"
    if [[ "$location" != /usr/lib/dotnet ]]; then
      echo "Expected Canonical runtime registration at /usr/lib/dotnet: $registration" >&2
      return 1
    fi
  done

  env -u DOTNET_ROOT -u DOTNET_ROOT_X64 -u DOTNET_HOST_PATH -u DOTNET_ROLL_FORWARD \
    LD_LIBRARY_PATH="$publish_dir" "$publish_dir/Miningcore" --version
)

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  if [[ "$#" -ne 1 ]]; then
    echo "usage: $0 PUBLISH_DIRECTORY" >&2
    exit 64
  fi
  validate_apphost_discovery "$1"
fi
