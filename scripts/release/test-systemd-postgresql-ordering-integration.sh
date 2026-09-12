#!/usr/bin/env bash

set -euo pipefail

# This uses the real system manager on a disposable GitHub-hosted VM, with inert
# service fixtures. It must never replace an operator's installed Miningcore.
if [[ ${1:-} != --disposable-host || $# -ne 1 ]]; then
    echo 'Usage: test-systemd-postgresql-ordering-integration.sh --disposable-host' >&2
    exit 64
fi
if [[ $EUID -ne 0 ]]; then
    echo 'Run as root on a disposable systemd-enabled test host.' >&2
    exit 77
fi
unset MININGCORE_SYSTEMD_ROOT MININGCORE_SYSTEMD_TEST_MODE
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
helper="$repository_root/packaging/systemd/configure-postgresql-ordering.sh"
# Avoid an instance name: hosts may already have a postgresql@.service template.
pg_unit=postgresql-miningcore-ordering-test.service
dropin_dir=/etc/systemd/system/miningcore.service.d
for unit in miningcore.service "$pg_unit"; do
    if [[ $(systemctl show "$unit" -p LoadState --value --no-pager) != not-found ]]; then
        echo "Refusing to replace existing unit: $unit" >&2
        exit 78
    fi
    for directory in /etc/systemd/system /run/systemd/system; do
        for path in "$directory/$unit" "$directory/$unit.d"; do
            if [[ -e "$path" || -L "$path" ]]; then
                echo "Refusing to alter existing service configuration: $path" >&2
                exit 78
            fi
        done
    done
done

fixture_dir=$(mktemp -d /run/miningcore-ordering-test.XXXXXX)
created_units=()
cleanup() {
    local status=$? cleanup_status=0
    trap - EXIT
    if [[ ${#created_units[@]} -gt 0 ]]; then
        systemctl stop "${created_units[@]}" || cleanup_status=1
    fi
    rm -f -- "$dropin_dir/postgresql-ordering.conf" "$dropin_dir/operator.conf" \
        /run/systemd/system/miningcore.service "/run/systemd/system/$pg_unit" || cleanup_status=1
    if [[ -d "$dropin_dir" ]]; then rmdir -- "$dropin_dir" || cleanup_status=1; fi
    systemctl daemon-reload || cleanup_status=1
    rm -rf -- "$fixture_dir" || cleanup_status=1
    if [[ $cleanup_status -ne 0 ]]; then
        echo "Fixture cleanup failed (original test exit status: $status)." >&2
        [[ $status -ne 0 ]] || status=$cleanup_status
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

cat > "$fixture_dir/record.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$1" >> "$(dirname "$0")/events"
printf 'ordering-test:%s\n' "$1"
EOF
for role in postgres miningcore; do
    unit=$pg_unit
    [[ "$role" != miningcore ]] || unit=miningcore.service
    created_units+=("$unit")
    cat > "/run/systemd/system/$unit" <<EOF
[Unit]
Description=Disposable Miningcore ordering test ($role)
[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/bash $fixture_dir/record.sh $role-start
ExecStop=/bin/bash $fixture_dir/record.sh $role-stop
TimeoutStartSec=15
TimeoutStopSec=15
EOF
done
systemctl daemon-reload

# Exercise the real discovery query without altering any runner database. The
# runner may have zero, one or several active Debian/Ubuntu clusters; all three
# results have defined dry-run behavior. Named fixtures below remain explicit.
discovery=$(systemctl list-units --type=service --state=active --no-legend --plain --no-pager 'postgresql@*.service')
mapfile -t discovered_units < <(printf '%s\n' "$discovery" | awk 'NF {print $1}' | sort -u)
status=0
output=$(bash "$helper" --dry-run 2>&1) || status=$?
case ${#discovered_units[@]} in
    0) [[ $status -eq 0 ]]; grep -Fq 'Skipping Miningcore/PostgreSQL ordering' <<< "$output" ;;
    1) [[ $status -eq 0 ]]; grep -Fxq "Selected local PostgreSQL unit: ${discovered_units[0]}" <<< "$output" ;;
    *) [[ $status -eq 78 ]]; grep -Fq 'refusing to guess' <<< "$output" ;;
esac
[[ ! -e "$dropin_dir" ]]
printf 'Real systemd discovery checked with %s active cluster(s).\n' "${#discovered_units[@]}"
bash "$helper" --unit "$pg_unit"

# Wants pulls PostgreSQL into this transaction; After determines execution order.
systemctl start miningcore.service
systemctl is-active --quiet miningcore.service
systemctl is-active --quiet "$pg_unit"
# Both stops must participate in the same transaction for reverse ordering.
systemctl stop "$pg_unit" miningcore.service
printf '%s\n' postgres-start miningcore-start miningcore-stop postgres-stop > "$fixture_dir/expected"
cmp "$fixture_dir/expected" "$fixture_dir/events"
journalctl --sync
journalctl -b -u miningcore.service -u "$pg_unit" --no-pager -o cat > "$fixture_dir/journal"
for event in postgres-start miningcore-start miningcore-stop postgres-stop; do
    grep -Fxq "ordering-test:$event" "$fixture_dir/journal"
done
cat "$fixture_dir/events"

# Prove surviving dependencies are detected by the real manager, not just mocks.
printf '[Unit]\nAfter=%s\n' "$pg_unit" > "$dropin_dir/operator.conf"
status=0
output=$(bash "$helper" --remove 2>&1) || status=$?
[[ $status -eq 78 ]]
grep -Fq "Remaining PostgreSQL dependency: After=$pg_unit" <<< "$output"
[[ ! -e "$dropin_dir/postgresql-ordering.conf" && -f "$dropin_dir/operator.conf" ]]
rm -- "$dropin_dir/operator.conf"
bash "$helper" --remove

# Unexpected configuration dependencies and exact acknowledgements also work
# against the live graph. An After-only exporter need not be started or exist.
printf '[Unit]\nAfter=postgresql-exporter.service\n' > "$dropin_dir/operator.conf"
status=0
output=$(bash "$helper" --unit "$pg_unit" 2>&1) || status=$?
[[ $status -eq 78 ]]
grep -Fq 'Remaining PostgreSQL dependency: After=postgresql-exporter.service' <<< "$output"
bash "$helper" --unit "$pg_unit" --allow-remaining postgresql-exporter.service
bash "$helper" --remove --allow-remaining postgresql-exporter.service
[[ -f "$dropin_dir/operator.conf" ]]
rm -- "$dropin_dir/operator.conf"
bash "$helper" --remove

# A masked unit may still be readable with systemctl cat; LoadState must reject it.
mv /run/systemd/system/miningcore.service "$fixture_dir/miningcore.service"
ln -s /dev/null /run/systemd/system/miningcore.service
systemctl daemon-reload
status=0
output=$(bash "$helper" --unit "$pg_unit" 2>&1) || status=$?
[[ $status -eq 69 ]]
grep -Fq 'not masked' <<< "$output"
rm /run/systemd/system/miningcore.service
mv "$fixture_dir/miningcore.service" /run/systemd/system/miningcore.service
systemctl daemon-reload

echo 'Real systemd PostgreSQL ordering integration tests passed'
