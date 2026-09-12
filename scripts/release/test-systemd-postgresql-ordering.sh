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
if grep -Eq 'daemon-reload|^show .* -p Wants' "$TEST_TRACE"; then exit 1; fi

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

# Persistent-state transitions exercise the real write, chmod, rename and reload.
umask 0077
expect 0 'Configured Miningcore ordering' bash "$helper"
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
expect 0 'Skipping Miningcore/PostgreSQL ordering' env FAKE_PG_UNITS=none bash "$helper"

# Removal must inspect all effective dependency tokens, preserve other files,
# report which properties remain, and succeed on retry only after correction.
for property in Wants After; do
    for unit in postgresql@17-main.service postgresql.service postgresql-16.service postgresql-custom.target; do
        printf '[Unit]\n%s=network-online.target %s basic.target\n' "$property" "$unit" > "$dropin_dir/operator.conf"
        cp "$dropin_dir/operator.conf" "$fixture_dir/operator.expected"
        expect 0 'Configured Miningcore ordering' bash "$helper"
        expect 78 "Remaining PostgreSQL dependency: $property=$unit" bash "$helper" --remove
        [[ ! -e "$dropin" ]]
        cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"
        expect 78 'PostgreSQL ordering remains' bash "$helper" --remove
        printf '[Service]\nRestart=no\n' > "$dropin_dir/operator.conf"
        expect 0 'No PostgreSQL dependencies remain' bash "$helper" --remove
    done
done
expect 69 'Unable to verify' env FAKE_SHOW_FAIL=1 bash "$helper" --remove
expect 78 'Verification failed' env FAKE_PROPERTIES=missing-property bash "$helper" --remove
expect 78 'Verification failed' env FAKE_PROPERTIES=duplicate-property bash "$helper" --remove
expect 0 'Removed managed PostgreSQL ordering' env FAKE_PROPERTIES=empty bash "$helper" --remove

ln -s "$fixture_dir/expected" "$dropin"
expect 78 'unsafe managed drop-in path' bash "$helper"
expect 78 'unsafe managed drop-in path' bash "$helper" --remove
cmp "$fixture_dir/expected" "$dropin"

echo 'systemd PostgreSQL ordering helper tests passed'
