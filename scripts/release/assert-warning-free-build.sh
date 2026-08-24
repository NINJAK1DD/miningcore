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

warning_pattern='^.*:[[:digit:]]+(:[[:digit:]]+)?: warning:'
warning_pattern+='|^.*\([[:digit:]]+(,[[:digit:]]+)?\): warning [A-Z][A-Z0-9]+:'
warning_pattern+='|^.*:[[:space:]]+warning [A-Z][A-Z0-9]+:'
warning_pattern+='|^(cc1|cc1plus|clang|clang\+\+|gcc|g\+\+|ld): warning:'
warning_pattern+='|^[[:space:]]*CMake([[:space:]]+[[:alpha:]_-]+)*'
warning_pattern+='[[:space:]]+Warning([[:space:](]|:)'

warning_matches=$(grep -En "$warning_pattern" "$build_log" || true)

if [[ -n "$warning_matches" ]]; then
  echo "Build emitted compiler or build-system warnings:" >&2
  printf '%s\n' "$warning_matches" >&2
  exit 1
fi

echo "Build warning audit passed"
