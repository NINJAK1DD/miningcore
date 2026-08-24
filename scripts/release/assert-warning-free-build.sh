#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 BUILD_LOG" >&2
  exit 64
fi

build_log=$1

if [[ ! -r "$build_log" ]]; then
  echo "Build warning audit could not read its private log" >&2
  exit 70
fi

warning_pattern='(^|[[:space:]])warning([[:space:]]+[[:alnum:]_-]+)?:|^CMake Warning'

if grep -Ein "$warning_pattern" "$build_log" > /dev/null; then
  echo "Build emitted compiler or build-system warnings:" >&2
  grep -Ein "$warning_pattern" "$build_log" >&2
  exit 1
fi

echo "Build warning audit passed"
