#!/usr/bin/env bash

set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 BUILD_LOG {build|publish} [DOTNET_ARGUMENT ...]" >&2
  exit 64
fi

build_log=$1
shift

case $1 in
  build|publish) ;;
  *)
    echo "Warning-audited dotnet command must be build or publish" >&2
    exit 64
    ;;
esac

if [[ ! -f "$build_log" || -L "$build_log" || ! -w "$build_log" ]]; then
  echo "Warning-audited build could not open its private log" >&2
  exit 70
fi

: > "$build_log"

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
file_logger="-flp:LogFile=$build_log;Verbosity=normal;Encoding=UTF-8"

# Keep stdout/stderr attached directly to the caller. Interactive source builds
# therefore retain .NET's concise terminal progress and elapsed-time display,
# while the separate MSBuild file logger captures the complete warning audit.
dotnet "$@" --tl:auto "$file_logger"

if [[ ! -s "$build_log" ]]; then
  echo "Warning-audited build did not produce its private log" >&2
  exit 70
fi

bash "$script_dir/audit-source-build-warnings.sh" "$build_log"
