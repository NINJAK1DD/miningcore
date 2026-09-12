#!/usr/bin/env bash

set -euo pipefail

# The helper retains its production root check even under the filesystem prefix.
# Only the private fixture is changed; every systemctl invocation is mocked.
if [[ $EUID -ne 0 ]]; then
    echo 'Run this regression suite with sudo bash (it includes root-only mutations).' >&2
    exit 77
fi
repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
helper="$repository_root/packaging/systemd/configure-postgresql-ordering.sh"
fixture_dir=$(mktemp -d)
trap 'rm -rf -- "$fixture_dir"' EXIT
mkdir -p "$fixture_dir/bin" "$fixture_dir/root" "$fixture_dir/empty-bin"
export MININGCORE_SYSTEMD_ROOT="$fixture_dir/root"
export MININGCORE_SYSTEMD_TEST_MODE=1
export TEST_TRACE="$fixture_dir/trace"
export TEST_STATE="$fixture_dir/state"
: > "$TEST_STATE"
dropin_dir="$MININGCORE_SYSTEMD_ROOT/etc/systemd/system/miningcore.service.d"
dropin="$dropin_dir/postgresql-ordering.conf"
export TEST_DROPIN="$dropin"

cat > "$fixture_dir/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "${TEST_TRACE:?}"
case "$*" in
    'list-units --type=service --state=active --no-legend --plain --no-pager postgresql@*.service')
        case "${FAKE_PG_UNITS:-one}" in
            none) ;;
            one) echo 'postgresql@17-main.service loaded active running PostgreSQL Cluster' ;;
            exited) echo 'postgresql@17-main.service loaded active exited PostgreSQL Cluster' ;;
            new) echo 'postgresql@18-main.service loaded active running PostgreSQL Cluster' ;;
            many)
                echo 'postgresql@17-main.service loaded active running PostgreSQL Cluster'
                echo 'postgresql@18-main.service loaded active running PostgreSQL Cluster'
                ;;
            invalid) echo 'ssh.service loaded active running Invalid discovery' ;;
            fail) exit 1 ;;
            *) exit 2 ;;
        esac
        ;;
    'show miningcore.service -p LoadState --value --no-pager')
        [[ ${FAKE_MININGCORE_STATUS:-0} == 0 ]] || exit 1
        echo "${FAKE_MININGCORE_LOAD_STATE:-loaded}"
        ;;
    'show postgresql@17-main.service -p LoadState --value --no-pager'|\
    'show postgresql@18-main.service -p LoadState --value --no-pager'|\
    'show postgresql.service -p LoadState --value --no-pager'|\
    'show postgresql-16.service -p LoadState --value --no-pager')
        echo "${FAKE_PG_LOAD_STATE:-loaded}"
        ;;
    'show postgresql@99-missing.service -p LoadState --value --no-pager') echo not-found ;;
    daemon-reload)
        [[ ${FAKE_RELOAD_FAIL:-0} == 0 ]] || exit 1
        # Model the manager loading the on-disk graph only after daemon-reload.
        if [[ -f "$TEST_DROPIN" ]]; then
            cp "$TEST_DROPIN" "$TEST_STATE"
        else
            : > "$TEST_STATE"
        fi
        if [[ -f "${TEST_DROPIN%/*}/operator.conf" ]]; then
            cat "${TEST_DROPIN%/*}/operator.conf" >> "$TEST_STATE"
        fi
        ;;
    'show miningcore.service -p Wants -p After --no-pager')
        [[ ${FAKE_SHOW_FAIL:-0} == 0 ]] || exit 1
        case "${FAKE_PROPERTIES:-valid}" in
            valid|first|last)
                wants=$(awk -F= '/^Wants=/ { printf "%s ", $2 }' "$TEST_STATE")
                after=$(awk -F= '/^After=/ { printf "%s ", $2 }' "$TEST_STATE")
                case "${FAKE_PROPERTIES:-valid}" in
                    first) printf 'Wants=%snetwork-online.target\nAfter=%sbasic.target sysinit.target\n' "$wants" "$after" ;;
                    last) printf 'Wants=network-online.target %s\nAfter=basic.target sysinit.target %s\n' "$wants" "$after" ;;
                    valid) printf 'Wants=network-online.target %ssockets.target\nAfter=basic.target %ssysinit.target system.slice\n' "$wants" "$after" ;;
                esac
                ;;
            missing-wants) echo 'Wants=network.target'; echo 'After=postgresql@17-main.service' ;;
            missing-after) echo 'Wants=postgresql@17-main.service'; echo 'After=network.target' ;;
            substring) echo 'Wants=postgresql@17-main.service.extra'; echo 'After=postgresql@17-main.service' ;;
            empty) printf 'Wants=\nAfter=\n' ;;
            missing-property) echo 'Wants=network-online.target' ;;
            duplicate-property) printf 'Wants=\nWants=\nAfter=\n' ;;
            wrong-service) printf 'Wants=postgresql@17-main.service\nAfter=postgresql@17-main.service\n' ;;
            *) exit 2 ;;
        esac
        ;;
    *) echo "Unexpected systemctl arguments: $*" >&2; exit 2 ;;
esac
EOF
chmod +x "$fixture_dir/bin/systemctl"
export PATH="$fixture_dir/bin:$PATH"

expect() {
    local expected=$1 message=$2
    shift 2
    local status=0
    output=$("$@" 2>&1) || status=$?
    if [[ $status -ne $expected ]] || ! grep -Fq -- "$message" <<< "$output"; then
        printf 'Expected exit %s containing %s; got %s:\n%s\n' "$expected" "$message" "$status" "$output" >&2
        exit 1
    fi
    if [[ $expected -ne 0 ]] && grep -Eq 'Configured Miningcore ordering|Removed managed PostgreSQL ordering:' <<< "$output"; then
        echo 'Failure incorrectly claimed successful configuration/removal' >&2
        exit 1
    fi
}

expect 0 'Wants=postgresql@17-main.service' bash "$helper" --dry-run
expect 0 'After=postgresql@17-main.service' env FAKE_PG_UNITS=exited bash "$helper" --dry-run
expect 0 'Skipping Miningcore/PostgreSQL ordering' env FAKE_PG_UNITS=none bash "$helper" --dry-run
expect 69 'Unable to query systemd' env FAKE_PG_UNITS=fail bash "$helper" --dry-run
expect 78 'refusing to guess' env FAKE_PG_UNITS=many bash "$helper" --dry-run
expect 64 'Refusing invalid PostgreSQL unit' env FAKE_PG_UNITS=invalid bash "$helper" --dry-run
expect 64 'Refusing invalid PostgreSQL unit' bash "$helper" --dry-run --unit ssh.service
expect 64 'Refusing invalid PostgreSQL unit' bash "$helper" --dry-run --unit $'postgresql@17-main.service\nRequires=ssh.service'
expect 69 'must exist and be loaded' bash "$helper" --dry-run --unit postgresql@99-missing.service
expect 64 'Missing or empty' bash "$helper" --dry-run --unit ''
expect 64 'Missing or empty' bash "$helper" --dry-run --unit
expect 64 'Missing or empty' bash "$helper" --unit --dry-run
expect 64 'Missing or empty' bash "$helper" --allow-remaining
expect 64 'Missing or empty' bash "$helper" --allow-remaining ''
expect 64 'Missing or empty' bash "$helper" --allow-remaining --remove
for invalid in 'postgresql*' ssh.service $'postgresql-exporter.service\nAfter=ssh.service'; do
    expect 64 'Invalid --allow-remaining' bash "$helper" --remove --allow-remaining "$invalid"
done
expect 64 'Duplicate --unit' bash "$helper" --unit postgresql.service --unit postgresql.service
expect 64 'Unknown argument' bash "$helper" --unexpected
expect 64 'mutually exclusive' bash "$helper" --remove --unit postgresql.service
expect 64 'absolute directory' env MININGCORE_SYSTEMD_ROOT=relative bash "$helper" --dry-run
expect 0 'Usage:' bash "$helper" -h
expect 0 'Usage:' bash "$helper" --help
for unit in postgresql.service postgresql-16.service postgresql@18-main.service; do
    expect 0 "Wants=$unit" env FAKE_PG_UNITS=many bash "$helper" --dry-run --unit "$unit"
done
expect 69 'must exist and be loaded' env FAKE_MININGCORE_STATUS=1 bash "$helper" --dry-run
expect 69 'must exist and be loaded' env FAKE_MININGCORE_STATUS=1 bash "$helper"
for state in masked not-found error; do
    expect 69 'not masked' env FAKE_MININGCORE_LOAD_STATE="$state" bash "$helper"
    expect 69 'not masked' env FAKE_PG_LOAD_STATE="$state" bash "$helper"
done
[[ ! -e "$dropin_dir" ]]
if grep -Fq 'daemon-reload' "$TEST_TRACE"; then exit 1; fi

# Accidental prefixes must fail before either discovery or mutation, even if the
# real manager would already have matching dependencies from an old configuration.
: > "$TEST_TRACE"
expect 64 'requires MININGCORE_SYSTEMD_TEST_MODE=1' env -u MININGCORE_SYSTEMD_TEST_MODE bash "$helper"
expect 64 'requires MININGCORE_SYSTEMD_TEST_MODE=1' env MININGCORE_SYSTEMD_TEST_MODE=0 bash "$helper" --remove
[[ ! -s "$TEST_TRACE" && ! -e "$dropin_dir" ]]

# Real non-root execution, including prefix: no environment EUID spoofing.
cp "$helper" "$fixture_dir/unprivileged-helper.sh"
chmod 0755 "$fixture_dir" "$fixture_dir/root"
chmod 0644 "$fixture_dir/unprivileged-helper.sh"
expect 77 'Run this helper as root' setpriv --reuid=65534 --regid=65534 --clear-groups \
    bash "$fixture_dir/unprivileged-helper.sh"
expect 77 'Run this helper as root' setpriv --reuid=65534 --regid=65534 --clear-groups \
    bash "$fixture_dir/unprivileged-helper.sh" --remove
expect 69 'systemctl is unavailable' env PATH="$fixture_dir/empty-bin" /bin/bash "$helper" --dry-run

# Preview reads the existing graph without demanding that a new selected unit
# already be present. Acknowledgements produce feedback but cannot hide bad data.
printf '[Unit]\nWants=postgresql-exporter.service\nAfter=postgresql-exporter.service postgresql@16-old.service\n' > "$TEST_STATE"
cp "$TEST_STATE" "$fixture_dir/preview.expected"
: > "$TEST_TRACE"
for operation in configure remove; do
    args=(--dry-run)
    if [[ "$operation" == remove ]]; then args+=(--remove); else args+=(--unit postgresql@18-main.service); fi
    expect 0 'Current PostgreSQL dependency to review: Wants=postgresql-exporter.service' bash "$helper" "${args[@]}"
    grep -Fq 'Current effective dependencies (before the proposed change):' <<< "$output"
    grep -Fq 'Exit 0 here does not guarantee post-change verification' <<< "$output"
    expect 0 'Acknowledged remaining PostgreSQL dependency: Wants=postgresql-exporter.service' bash "$helper" "${args[@]}" --allow-remaining postgresql-exporter.service
    grep -Fq 'Current PostgreSQL dependency to review: After=postgresql@16-old.service' <<< "$output"
    expect 0 'Current PostgreSQL dependency to review: Wants=postgresql-exporter.service' bash "$helper" "${args[@]}" --allow-remaining postgresql-exporter.service.extra
    expect 69 'Dry run made no changes' env FAKE_SHOW_FAIL=1 bash "$helper" "${args[@]}"
    for properties in missing-property duplicate-property; do
        expect 78 'Dry run made no changes' env FAKE_PROPERTIES="$properties" bash "$helper" "${args[@]}" --allow-remaining postgresql-exporter.service
    done
    expect 0 'Dry run made no changes' env FAKE_PROPERTIES=empty bash "$helper" "${args[@]}"
    cmp "$fixture_dir/preview.expected" "$TEST_STATE"
    [[ ! -e "$dropin_dir" ]]
done
if grep -Fq 'daemon-reload' "$TEST_TRACE"; then exit 1; fi

# The read-only path also succeeds under a genuinely unprivileged UID. Only the
# mock's separate trace is writable; all graph data and the helper stay read-only.
: > "$fixture_dir/unprivileged-trace"
chmod 0666 "$fixture_dir/unprivileged-trace"
for operation in configure remove; do
    args=(--dry-run)
    if [[ "$operation" == remove ]]; then args+=(--remove); else args+=(--unit postgresql@18-main.service); fi
    expect 0 'Acknowledged remaining PostgreSQL dependency' env TEST_TRACE="$fixture_dir/unprivileged-trace" \
        setpriv --reuid=65534 --regid=65534 --clear-groups \
        bash "$fixture_dir/unprivileged-helper.sh" "${args[@]}" --allow-remaining postgresql-exporter.service
done
cmp "$fixture_dir/preview.expected" "$TEST_STATE"
[[ ! -e "$dropin_dir" ]]
if grep -Fq 'daemon-reload' "$fixture_dir/unprivileged-trace"; then exit 1; fi
: > "$TEST_STATE"

# Persistent-state transitions exercise the real write, chmod, rename and reload.
umask 0077
expect 0 'Configured Miningcore ordering' bash "$helper"
[[ "$output" == 'Configured Miningcore ordering'* ]]
grep -q '^Wants=' <<< "$output"
grep -q '^After=' <<< "$output"
printf '[Unit]\nWants=postgresql@17-main.service\nAfter=postgresql@17-main.service\n' > "$fixture_dir/expected"
cmp "$fixture_dir/expected" "$dropin"
[[ $(stat -c %a "$dropin") == 644 && $(stat -c %a "$dropin_dir") == 755 ]]
expect 0 'Configured Miningcore ordering' bash "$helper"
cmp "$fixture_dir/expected" "$dropin"
chmod 0700 "$dropin_dir"
for properties in valid first last; do
    expect 0 'Configured Miningcore ordering' env FAKE_PROPERTIES="$properties" bash "$helper"
    [[ $(stat -c %a "$dropin_dir") == 700 ]]
done
printf '[Service]\nRestart=no\n' > "$dropin_dir/operator.conf"
cp "$dropin_dir/operator.conf" "$fixture_dir/operator.expected"
: > "$TEST_TRACE"
expect 78 'existing drop-in remains' env FAKE_PG_UNITS=none bash "$helper"
expect 78 'existing drop-in remains' env FAKE_PG_UNITS=none bash "$helper" --dry-run
expect 78 'refusing to guess' env FAKE_PG_UNITS=many bash "$helper"
expect 69 'Unable to query systemd' env FAKE_PG_UNITS=fail bash "$helper"
cmp "$fixture_dir/expected" "$dropin"
if grep -Eq 'daemon-reload|^show .* -p Wants' "$TEST_TRACE"; then exit 1; fi
expect 0 'Would remove' bash "$helper" --remove --dry-run
cmp "$fixture_dir/expected" "$dropin"
expect 0 'Configured Miningcore ordering' env FAKE_PG_UNITS=new bash "$helper"
grep -Fxq 'After=postgresql@18-main.service' "$dropin"
if grep -Fq '17-main' "$dropin"; then exit 1; fi
expect 0 'Configured Miningcore ordering' env FAKE_PG_UNITS=many bash "$helper" --unit postgresql@17-main.service
cmp "$fixture_dir/expected" "$dropin"

expect 69 'Unable to reload systemd' env FAKE_RELOAD_FAIL=1 bash "$helper"
expect 69 'Unable to verify' env FAKE_SHOW_FAIL=1 bash "$helper"
for properties in missing-wants missing-after substring empty missing-property duplicate-property; do
    expect 78 'Verification failed' env FAKE_PROPERTIES="$properties" bash "$helper"
    grep -Fq 'managed file has changed and systemd was reloaded' <<< "$output"
done
expect 78 'Verification failed' env FAKE_PROPERTIES=wrong-service bash "$helper" --unit postgresql.service
expect 0 'Configured Miningcore ordering' bash "$helper"

# Failed atomic replacement must retain the old complete file and clean its temp.
cat > "$fixture_dir/bin/mv" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
chmod +x "$fixture_dir/bin/mv"
: > "$TEST_TRACE"
expect 74 'Unable to install' env FAKE_PG_UNITS=new bash "$helper"
cmp "$fixture_dir/expected" "$dropin"
if grep -Fq 'daemon-reload' "$TEST_TRACE"; then exit 1; fi
rm "$fixture_dir/bin/mv"
[[ -z $(find "$dropin_dir" -name '*.tmp.*' -print) ]]

expect 69 'Unable to reload systemd' env FAKE_RELOAD_FAIL=1 bash "$helper" --remove
[[ ! -e "$dropin" ]]
expect 0 'Removed managed PostgreSQL ordering' env FAKE_MININGCORE_STATUS=1 bash "$helper" --remove
if grep -Fq 'postgresql' "$TEST_STATE"; then exit 1; fi
cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"
expect 0 'Removed managed PostgreSQL ordering' bash "$helper" --remove
[[ "$output" == 'Removed managed PostgreSQL ordering'* ]]
if grep -Eq '^(Wants|After)=' <<< "$output"; then exit 1; fi
expect 0 'Skipping Miningcore/PostgreSQL ordering' env FAKE_PG_UNITS=none bash "$helper"

# Removal must inspect all effective dependency tokens, preserve other files,
# report which properties remain, and succeed on retry only after correction.
for property in Wants After; do
    for unit in postgresql@17-main.service postgresql.service postgresql-16.service postgresql-custom.target; do
        printf '[Unit]\n%s=network-online.target %s basic.target\n' "$property" "$unit" > "$dropin_dir/operator.conf"
        cp "$dropin_dir/operator.conf" "$fixture_dir/operator.expected"
        # Configure now rejects other PostgreSQL dependencies as well. Explicit
        # acknowledgements may preserve them but cannot waive the selected unit.
        if [[ "$unit" != postgresql@17-main.service ]]; then
            expect 78 "Remaining PostgreSQL dependency: $property=$unit" bash "$helper"
            cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"
        fi
        expect 0 'Configured Miningcore ordering' bash "$helper" --allow-remaining "$unit"
        expect 78 "Remaining PostgreSQL dependency: $property=$unit" bash "$helper" --remove
        [[ ! -e "$dropin" ]]
        cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"
        expect 78 'PostgreSQL ordering remains' bash "$helper" --remove
        printf '[Service]\nRestart=no\n' > "$dropin_dir/operator.conf"
        expect 0 'No unacknowledged PostgreSQL dependencies remain' bash "$helper" --remove
    done
done
expect 69 'Unable to verify' env FAKE_SHOW_FAIL=1 bash "$helper" --remove
expect 78 'Verification failed' env FAKE_PROPERTIES=missing-property bash "$helper" --remove
expect 78 'Verification failed' env FAKE_PROPERTIES=duplicate-property bash "$helper" --remove
expect 0 'Removed managed PostgreSQL ordering' env FAKE_PROPERTIES=empty bash "$helper" --remove

# Upgrade with an operator-owned 17-main relationship must not silently succeed.
printf '[Unit]\nWants=postgresql@17-main.service\nAfter=postgresql@17-main.service\n' > "$dropin_dir/operator.conf"
cp "$dropin_dir/operator.conf" "$fixture_dir/operator.expected"
expect 78 'Remaining PostgreSQL dependency: Wants=postgresql@17-main.service' bash "$helper" --unit postgresql@18-main.service
grep -Fq 'Remaining PostgreSQL dependency: After=postgresql@17-main.service' <<< "$output"
grep -Fxq 'Wants=postgresql@18-main.service' "$dropin"
cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"

# Exact, per-invocation acknowledgements permit intentional exporter dependencies
# in both properties without swallowing a second unexpected service or a prefix.
printf '[Unit]\nWants=postgresql-exporter.service\nAfter=postgresql-exporter.service postgresql-metrics.target\n' > "$dropin_dir/operator.conf"
cp "$dropin_dir/operator.conf" "$fixture_dir/operator.expected"
for operation in configure remove; do
    args=()
    [[ "$operation" != remove ]] || args+=(--remove)
    expect 78 'Remaining PostgreSQL dependency' bash "$helper" "${args[@]}"
    expect 78 'Remaining PostgreSQL dependency: Wants=postgresql-exporter.service' bash "$helper" "${args[@]}" --allow-remaining postgresql-exporter.service.extra
    expect 78 'Remaining PostgreSQL dependency: After=postgresql-metrics.target' bash "$helper" "${args[@]}" --allow-remaining postgresql-exporter.service
    expect 0 'Acknowledged remaining PostgreSQL dependency' bash "$helper" "${args[@]}" \
        --allow-remaining postgresql-exporter.service --allow-remaining postgresql-metrics.target
    cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"
    expect 78 'Remaining PostgreSQL dependency' bash "$helper" "${args[@]}"
done
expect 78 'Verification failed' env FAKE_PROPERTIES=missing-wants bash "$helper" --allow-remaining postgresql@17-main.service
expect 78 'Verification failed' env FAKE_PROPERTIES=duplicate-property bash "$helper" --remove --allow-remaining postgresql-exporter.service
expect 69 'Unable to verify' env FAKE_SHOW_FAIL=1 bash "$helper" --remove --allow-remaining postgresql-exporter.service
printf '[Service]\nRestart=no\n' > "$dropin_dir/operator.conf"
expect 0 'Removed managed PostgreSQL ordering' bash "$helper" --remove

ln -s "$fixture_dir/expected" "$dropin"
expect 78 'unsafe managed drop-in path' bash "$helper"
expect 78 'unsafe managed drop-in path' bash "$helper" --remove
cmp "$fixture_dir/expected" "$dropin"

# Exercise the integration test's actual cleanup function with failed external
# commands, without touching the live system manager or service files.
awk '
    /^cleanup\(\) \{$/ { capture=1 }
    capture { print }
    capture && /^}$/ { exit }
' "$repository_root/scripts/release/test-systemd-postgresql-ordering-integration.sh" > "$fixture_dir/cleanup-test.sh"
cat >> "$fixture_dir/cleanup-test.sh" <<'EOF'
set -euo pipefail
created_units=()
if [[ ${CLEANUP_CREATED:-0} == 1 ]]; then created_units=(miningcore.service postgresql-fixture.service); fi
pg_unit=postgresql-fixture.service
fixture_dir=${MININGCORE_SYSTEMD_ROOT:?}
dropin_dir=$fixture_dir
systemctl() { printf '%s\n' "$*" >> "${CLEANUP_TRACE:?}"; return 5; }
rm() { return 1; }
rmdir() { return 1; }
trap cleanup EXIT
exit "${CLEANUP_INITIAL_STATUS:?}"
EOF
export CLEANUP_TRACE="$fixture_dir/cleanup-trace"
expect 74 'original test exit status: 74' env CLEANUP_INITIAL_STATUS=74 bash "$fixture_dir/cleanup-test.sh"
if grep -q '^stop ' "$CLEANUP_TRACE"; then exit 1; fi
expect 69 'original test exit status: 69' env CLEANUP_INITIAL_STATUS=69 CLEANUP_CREATED=1 bash "$fixture_dir/cleanup-test.sh"
grep -Fq 'stop miningcore.service postgresql-fixture.service' "$CLEANUP_TRACE"
expect 1 'original test exit status: 0' env CLEANUP_INITIAL_STATUS=0 CLEANUP_CREATED=1 bash "$fixture_dir/cleanup-test.sh"

echo 'systemd PostgreSQL ordering helper tests passed'
