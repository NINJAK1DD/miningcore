#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 BUILD_LOG" >&2
  exit 64
fi

build_log=$1

if [[ ! -f "$build_log" || ! -r "$build_log" ]]; then
  echo "Build warning audit could not read its private log" >&2
  exit 70
fi

warning_pattern='^.*:[[:digit:]]+(:[[:digit:]]+)?: warning:'
warning_pattern+='|^.*\([[:digit:]]+(,[[:digit:]]+)?\): warning [A-Z][A-Z0-9]+:'
warning_pattern+='|^.*:[[:space:]]+warning [A-Z][A-Z0-9]+:'
warning_pattern+='|^([^[:space:]:]*/)?'
warning_pattern+='(cc1|cc1plus|clang|clang\+\+|gcc|g\+\+|collect2|'
warning_pattern+='ld(\.(bfd|gold|lld))?|ar|as|ranlib): warning:'
warning_pattern+='|^[[:space:]]*CMake([[:space:]]+[[:alpha:]_-]+)*'
warning_pattern+='[[:space:]]+Warning([[:space:](]|:)'

set +e
warning_matches=$(grep -En -- "$warning_pattern" "$build_log" 2>/dev/null)
grep_status=$?
set -e

if [[ "$grep_status" -gt 1 ]]; then
  echo "Build warning audit could not process its private log" >&2
  exit 70
fi

if [[ -n "$warning_matches" ]]; then
  echo "Build emitted compiler or build-system warnings:" >&2
  printf '%s\n' "$warning_matches" >&2
  exit 1
fi

echo "Build warning audit passed"
