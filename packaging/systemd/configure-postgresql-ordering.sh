#!/usr/bin/env bash

set -euo pipefail

usage() {
    cat <<'EOF'
Usage: configure-postgresql-ordering.sh [--unit UNIT] [--dry-run]

Configure a Miningcore systemd drop-in so a local PostgreSQL cluster starts
before Miningcore and stops after Miningcore.

Without --unit, the helper auto-detects running postgresql@*.service units:
  - exactly one: configure it automatically
  - none: skip safely (remote/non-standard PostgreSQL may be intentional)
  - more than one: refuse to guess and require --unit

--dry-run performs discovery and prints the drop-in without writing files or
reloading systemd. It is also used by the release regression tests.
EOF
}

explicit_unit=
dry_run=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --unit)
            shift
            [[ $# -gt 0 ]] || { echo "Missing value for --unit" >&2; exit 64; }
            explicit_unit="$1"
            ;;
        --dry-run)
            dry_run=1
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 64
            ;;
    esac
    shift
done

if [[ $dry_run -eq 0 && ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "Run this helper as root (for example with sudo)." >&2
    exit 77
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl is unavailable; cannot configure local systemd ordering." >&2
    exit 69
fi

validate_unit() {
    local unit=$1

    if [[ ! "$unit" =~ ^postgresql@[A-Za-z0-9_.:-]+\.service$ ]]; then
        echo "Refusing invalid PostgreSQL cluster unit: $unit" >&2
        exit 64
    fi

    if ! systemctl cat "$unit" >/dev/null 2>&1; then
        echo "PostgreSQL cluster unit does not exist: $unit" >&2
        exit 69
    fi
}

pg_unit=
if [[ -n "$explicit_unit" ]]; then
    validate_unit "$explicit_unit"
    pg_unit="$explicit_unit"
else
    mapfile -t pg_units < <(
        systemctl list-units \
            --type=service \
            --state=running \
            --no-legend \
            --plain \
            'postgresql@*.service' |
        awk '{ print $1 }' |
        sort -u
    )

    case "${#pg_units[@]}" in
        0)
            echo "No running local postgresql@*.service cluster was detected."
            echo "Skipping Miningcore/PostgreSQL ordering; this is expected for remote or non-standard PostgreSQL deployments."
            exit 0
            ;;
        1)
            pg_unit="${pg_units[0]}"
            validate_unit "$pg_unit"
            ;;
        *)
            echo "Multiple running local PostgreSQL clusters were detected; refusing to guess:" >&2
            printf '  %s\n' "${pg_units[@]}" >&2
            echo "Re-run with --unit <postgresql@...service> for the cluster used by Miningcore." >&2
            exit 78
            ;;
    esac
fi

render_dropin() {
    cat <<EOF
[Unit]
Wants=$pg_unit
After=$pg_unit
EOF
}

if [[ $dry_run -eq 1 ]]; then
    echo "Detected local PostgreSQL cluster: $pg_unit"
    render_dropin
    exit 0
fi

dropin_dir=/etc/systemd/system/miningcore.service.d
dropin="$dropin_dir/postgresql-ordering.conf"

install -d -m 0755 "$dropin_dir"

tmp=$(mktemp "${dropin}.tmp.XXXXXX")
trap 'rm -f -- "$tmp"' EXIT
render_dropin > "$tmp"
install -m 0644 "$tmp" "$dropin"
rm -f -- "$tmp"
trap - EXIT

systemctl daemon-reload

echo "Configured Miningcore ordering with local PostgreSQL cluster: $pg_unit"
echo "Drop-in: $dropin"
systemctl show miningcore -p Wants -p After --no-pager
