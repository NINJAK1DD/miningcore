#!/usr/bin/env bash

knots_postgres_bin() {
  local root=$1 selected
  local -a candidates=()
  for selected in "$root"/*/bin/initdb; do
    [[ -x "$selected" ]] && candidates+=("$selected")
  done
  if (( ${#candidates[@]} == 0 )); then
    echo "No executable PostgreSQL initdb under $root; set MININGCORE_TEST_POSTGRES_BIN." >&2
    return 1
  fi
  selected=$(printf '%s\n' "${candidates[@]}" | sort -V | tail -n 1)
  dirname -- "$selected"
}

knots_run_logged_gate() {
  local log=$1 script=$2 status
  if bash "$script" > "$log" 2>&1; then return 0; else status=$?; fi
  echo "Knots upstream gate failed (exit $status). Full diagnostics: $log; last 200 lines follow." >&2
  tail -n 200 -- "$log" >&2 || true
  return "$status"
}
