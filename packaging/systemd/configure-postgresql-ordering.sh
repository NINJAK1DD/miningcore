#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: configure-postgresql-ordering.sh [--unit UNIT | --remove] [--dry-run]
                                      [--allow-remaining UNIT]...

Order a local PostgreSQL service before Miningcore at startup and after it at
shutdown. Run only when Miningcore uses the selected local database.

Without --unit, discover active Debian/Ubuntu postgresql@*.service clusters:
  - one: configure it; multiple: refuse to guess and require --unit
  - none: skip only if no existing managed drop-in remains
Other local layouts require --unit (for example postgresql-16.service).
--remove explicitly removes only postgresql-ordering.conf, then reloads systemd.
After a change, both operations exit 78 for unexpected PostgreSQL dependencies.
--allow-remaining UNIT acknowledges one intentional remaining dependency by exact
name, for this invocation only. Repeat for multiple units; wildcards are refused.
--dry-run validates and previews changes without writing or reloading systemd.
It reports current dependencies and acknowledgements, not a predicted final graph.
Valid previews exit 0 even when listing current dependencies for review.
Rerun after changing the PostgreSQL major version or cluster unit name.

MININGCORE_SYSTEMD_ROOT is a filesystem prefix for isolated regression tests.
It requires MININGCORE_SYSTEMD_TEST_MODE=1 and a mocked systemctl.
Neither variable bypasses root authorization or redirects the systemd manager.
Requires Bash, systemd and GNU coreutils on the local host.
EOF
}

explicit_unit=
dry_run=0
remove=0
allowed_remaining=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --unit)
            shift
            [[ $# -gt 0 && -n "$1" && "$1" != --* ]] || {
                echo 'Missing or empty value for --unit' >&2; exit 64;
            }
            [[ -z "$explicit_unit" ]] || { echo 'Duplicate --unit' >&2; exit 64; }
            explicit_unit=$1
            ;;
        --allow-remaining)
            shift
            [[ $# -gt 0 && -n "$1" && "$1" != --* ]] || {
                echo 'Missing or empty value for --allow-remaining' >&2; exit 64;
            }
            if [[ ! "$1" =~ ^postgresql[A-Za-z0-9_.:@-]*\.[a-z]+$ ]]; then
                echo "Invalid --allow-remaining unit (use an exact PostgreSQL-prefixed name): $1" >&2
                exit 64
            fi
            allowed_remaining+=("$1")
            ;;
        --remove) remove=1 ;;
        --dry-run) dry_run=1 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 64 ;;
    esac
    shift
done
if [[ $remove -eq 1 && -n "$explicit_unit" ]]; then
    echo '--remove and --unit are mutually exclusive' >&2
    exit 64
fi

root=${MININGCORE_SYSTEMD_ROOT:-}
if [[ -n "$root" && ${MININGCORE_SYSTEMD_TEST_MODE:-} != 1 ]]; then
    echo 'MININGCORE_SYSTEMD_ROOT requires MININGCORE_SYSTEMD_TEST_MODE=1 and a mocked systemctl; unset the prefix for normal operation.' >&2
    exit 64
fi
if [[ -n "$root" && ( "$root" != /* || ! -d "$root" ) ]]; then
    echo 'MININGCORE_SYSTEMD_ROOT must be an existing absolute directory' >&2
    exit 64
fi
dropin_dir="${root%/}/etc/systemd/system/miningcore.service.d"
dropin="$dropin_dir/postgresql-ordering.conf"

if [[ $dry_run -eq 0 && $EUID -ne 0 ]]; then
    echo 'Run this helper as root (for example with sudo).' >&2
    exit 77
fi
if ! command -v systemctl >/dev/null 2>&1; then
    echo 'systemctl is unavailable; cannot configure local systemd ordering.' >&2
    exit 69
fi
if [[ -L "$dropin_dir" || -L "$dropin" || ( -e "$dropin" && ! -f "$dropin" ) ]]; then
    echo "Refusing unsafe managed drop-in path: $dropin" >&2
    exit 78
fi

reload_systemd() {
    if ! systemctl daemon-reload; then
        echo "Unable to reload systemd; inspect $dropin and retry before starting Miningcore." >&2
        exit 69
    fi
}

read_dependencies() {
    local phase=${1:-changed} properties
    if ! properties=$(systemctl show miningcore.service -p Wants -p After --no-pager); then
        if [[ "$phase" == preview ]]; then
            echo 'Unable to read current Miningcore dependencies. Dry run made no changes.' >&2
        else
            echo 'Unable to verify Miningcore dependencies. The managed file has changed and systemd was reloaded; inspect the effective configuration before starting Miningcore.' >&2
        fi
        exit 69
    fi
    printf '%s\n' "$properties"
}

verify_dependencies() {
    local operation=$1 selected_unit=$2 properties=$3
    shift 3
    local property line dependency seen found acknowledged allowed remaining=0
    local -a dependencies allowed_units=("$@")
    for property in Wants After; do
        seen=0
        found=0
        while IFS= read -r line; do
            if [[ "$line" == "$property="* ]]; then
                seen=$((seen + 1))
                read -r -a dependencies <<< "${line#*=}"
                for dependency in "${dependencies[@]}"; do
                    if [[ "$operation" == *configure && "$dependency" == "$selected_unit" ]]; then
                        found=1
                    elif [[ "$dependency" == postgresql* ]]; then
                        acknowledged=0
                        for allowed in "${allowed_units[@]}"; do
                            [[ "$dependency" != "$allowed" ]] || acknowledged=1
                        done
                        if [[ $acknowledged -eq 1 ]]; then
                            echo "Acknowledged remaining PostgreSQL dependency: $property=$dependency" >&2
                        else
                            if [[ "$operation" == preview-* ]]; then
                                echo "Current PostgreSQL dependency to review: $property=$dependency" >&2
                            else
                                echo "Remaining PostgreSQL dependency: $property=$dependency" >&2
                            fi
                            remaining=1
                        fi
                    fi
                done
            fi
        done <<< "$properties"
        if [[ $seen -ne 1 || ( "$operation" == configure && $found -ne 1 ) ]]; then
            if [[ "$operation" == preview-* ]]; then
                echo "Verification failed for $property in the current graph. Dry run made no changes." >&2
            else
                echo "Verification failed for $property. The managed file has changed and systemd was reloaded; inspect $dropin and other drop-ins before starting Miningcore." >&2
            fi
            exit 78
        fi
    done
    if [[ $remaining -ne 0 && "$operation" != preview-* ]]; then
        echo 'The managed-file operation completed and systemd was reloaded, but unexpected PostgreSQL ordering remains. Inspect systemctl cat miningcore.service; remove obsolete entries or acknowledge intentional units with --allow-remaining UNIT.' >&2
        exit 78
    fi
}

preview_dependencies() {
    local operation=$1 selected_unit=$2 properties
    properties=$(read_dependencies preview)
    echo 'Current effective dependencies (before the proposed change):'
    printf '%s\n' "$properties"
    if [[ ${#allowed_remaining[@]} -gt 0 ]]; then
        printf 'Acknowledgement supplied for this invocation: %s\n' "${allowed_remaining[@]}"
    fi
    verify_dependencies "preview-$operation" "$selected_unit" "$properties" "${allowed_remaining[@]}"
    echo 'Dry run made no changes. Current entries may disappear when the managed drop-in is replaced or removed.'
    echo 'Review extras: any that survive the change require removal or --allow-remaining UNIT. Exit 0 here does not guarantee post-change verification.'
}

require_loaded_unit() {
    local unit=$1 load_state
    if ! load_state=$(systemctl show "$unit" -p LoadState --value --no-pager) ||
        [[ "$load_state" != loaded ]]; then
        echo "Unit must exist and be loaded (not masked): $unit. Install/unmask it and run systemctl daemon-reload before configuring ordering." >&2
        exit 69
    fi
}

if [[ $remove -eq 1 ]]; then
    if [[ $dry_run -eq 1 ]]; then
        echo "Would remove managed drop-in (if present) and reload systemd: $dropin"
        preview_dependencies remove ''
        exit 0
    fi
    # Also reload when absent, so retrying after a failed reload is effective.
    rm -f -- "$dropin" || { echo "Unable to remove $dropin" >&2; exit 74; }
    reload_systemd
    properties=$(read_dependencies)
    verify_dependencies remove '' "$properties" "${allowed_remaining[@]}"
    echo "Removed managed PostgreSQL ordering: $dropin"
    echo 'No unacknowledged PostgreSQL dependencies remain in Wants or After. Other drop-ins are unchanged.'
    exit 0
fi

validate_unit() {
    local unit=$1
    if [[ ! "$unit" =~ ^postgresql([@-][A-Za-z0-9_.:-]+)?\.service$ ]]; then
        echo "Refusing invalid PostgreSQL unit: $unit" >&2
        exit 64
    fi
    require_loaded_unit "$unit"
}

pg_unit=$explicit_unit
if [[ -z "$pg_unit" ]]; then
    if ! discovery_output=$(systemctl list-units --type=service --state=active \
        --no-legend --plain --no-pager 'postgresql@*.service'); then
        echo 'Unable to query systemd for local PostgreSQL clusters; ordering was not changed.' >&2
        exit 69
    fi
    mapfile -t pg_units < <(printf '%s\n' "$discovery_output" | awk 'NF { print $1 }' | sort -u)
    case "${#pg_units[@]}" in
        0)
            if [[ -e "$dropin" ]]; then
                echo "No active Debian/Ubuntu cluster found, but an existing drop-in remains: $dropin" >&2
                echo 'Start/select the replacement cluster and rerun with --unit; use --remove for a confirmed migration to remote PostgreSQL.' >&2
                exit 78
            fi
            echo 'No active Debian/Ubuntu postgresql@*.service cluster was detected.'
            echo 'Skipping Miningcore/PostgreSQL ordering. Other local layouts need --unit or a reviewed manual drop-in; remote databases need no local dependency.'
            if [[ $dry_run -eq 1 ]]; then preview_dependencies remove ''; fi
            exit 0
            ;;
        1) pg_unit=${pg_units[0]} ;;
        *)
            echo 'Multiple active local PostgreSQL clusters were detected; refusing to guess:' >&2
            printf '  %s\n' "${pg_units[@]}" >&2
            echo 'Rerun with --unit for the cluster used by Miningcore. Existing ordering was not changed.' >&2
            exit 78
            ;;
    esac
fi
validate_unit "$pg_unit"
require_loaded_unit miningcore.service

render_dropin() {
    cat <<EOF
[Unit]
Wants=$pg_unit
After=$pg_unit
EOF
}
if [[ $dry_run -eq 1 ]]; then
    echo "Selected local PostgreSQL unit: $pg_unit"
    render_dropin
    preview_dependencies configure "$pg_unit"
    exit 0
fi

if [[ ! -d "$dropin_dir" ]]; then
    install -d -m 0755 "$dropin_dir" || exit 74
fi
tmp=$(mktemp "${dropin}.tmp.XXXXXX") || exit 74
trap 'rm -f -- "$tmp"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
if ! render_dropin > "$tmp" || ! chmod 0644 "$tmp" || ! mv -fT -- "$tmp" "$dropin"; then
    echo "Unable to install $dropin" >&2
    exit 74
fi

reload_systemd
properties=$(read_dependencies)
verify_dependencies configure "$pg_unit" "$properties" "${allowed_remaining[@]}"
echo "Configured Miningcore ordering with local PostgreSQL unit: $pg_unit"
echo "Drop-in: $dropin"
printf '%s\n' "$properties"
