#!/usr/bin/env bash

set -euo pipefail
trap 'echo "Systemd integration test failed at line $LINENO: $BASH_COMMAND" >&2' ERR

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
test_target=miningcore-ordering-test.target
dropin_dir=/etc/systemd/system/miningcore.service.d
for unit in miningcore.service "$pg_unit" "$test_target"; do
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
        /run/systemd/system/miningcore.service "/run/systemd/system/$pg_unit" \
        "/run/systemd/system/$test_target" || cleanup_status=1
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
PartOf=$test_target
[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/bash $fixture_dir/record.sh $role-start
ExecStop=/bin/bash $fixture_dir/record.sh $role-stop
TimeoutStartSec=15
TimeoutStopSec=15
EOF
done
created_units+=("$test_target")
cat > "/run/systemd/system/$test_target" <<EOF
[Unit]
Description=Disposable transaction for Miningcore ordering test
Wants=miningcore.service
After=miningcore.service
EOF
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
systemctl start "$test_target"
systemctl is-active --quiet miningcore.service
systemctl is-active --quiet "$pg_unit"
# Older systemctl versions submit a multi-unit stop as separate D-Bus requests.
# A single target stop propagates through PartOf to create one shared transaction.
systemctl stop "$test_target"
# systemctl waits for the requested target job, which can finish before its
# propagated stop jobs. Observe both services without submitting new stop jobs
# that might accidentally impose the order this test is meant to verify.
stop_deadline=$((SECONDS + 35))
while [[ $(systemctl show miningcore.service -p ActiveState --value) != inactive ||
         $(systemctl show "$pg_unit" -p ActiveState --value) != inactive ]]; do
    if [[ $SECONDS -ge $stop_deadline ]]; then
        echo 'Timed out waiting for the propagated fixture stop jobs.' >&2
        systemctl status miningcore.service "$pg_unit" --no-pager >&2 || true
        exit 1
    fi
    sleep 0.1
done
printf '%s\n' postgres-start miningcore-start miningcore-stop postgres-stop > "$fixture_dir/expected"
if ! cmp "$fixture_dir/expected" "$fixture_dir/events"; then
    echo 'Unexpected lifecycle events:' >&2
    cat "$fixture_dir/events" >&2
    exit 1
fi
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
systemctl daemon-reload
# Property token order is not part of systemd's dependency contract.
dependency_snapshot() {
    local property
    for property in Wants After; do
        printf '%s\n' "$property="
        systemctl show miningcore.service -p "$property" --value --no-pager | tr ' ' '\n' | LC_ALL=C sort
    done
}
graph_before=$(dependency_snapshot)
cp "$dropin_dir/operator.conf" "$fixture_dir/operator.expected"
for operation in configure remove; do
    args=(--dry-run)
    if [[ "$operation" == remove ]]; then args+=(--remove); else args+=(--unit "$pg_unit"); fi
    output=$(bash "$helper" "${args[@]}" 2>&1)
    printf '%s\n' "$output"
    grep -Fq 'Current PostgreSQL dependency to review: After=postgresql-exporter.service' <<< "$output"
    output=$(bash "$helper" "${args[@]}" --allow-remaining postgresql-exporter.service 2>&1)
    printf '%s\n' "$output"
    grep -Fq 'Acknowledged remaining PostgreSQL dependency: After=postgresql-exporter.service' <<< "$output"
    [[ ! -e "$dropin_dir/postgresql-ordering.conf" ]]
    cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"
    graph_after=$(dependency_snapshot)
    if [[ "$graph_after" != "$graph_before" ]]; then
        printf 'Preview changed dependency membership:\nBefore:\n%s\nAfter:\n%s\n' "$graph_before" "$graph_after" >&2
        exit 1
    fi
done
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
