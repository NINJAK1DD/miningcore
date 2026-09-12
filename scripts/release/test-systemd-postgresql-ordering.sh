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
    'cat miningcore.service --no-pager') exit "${FAKE_MININGCORE_STATUS:-0}" ;;
    'cat postgresql@17-main.service --no-pager'|'cat postgresql@18-main.service --no-pager'|\
    'cat postgresql.service --no-pager'|'cat postgresql-16.service --no-pager') exit 0 ;;
    'cat postgresql@99-missing.service --no-pager') exit 1 ;;
    daemon-reload)
        [[ ${FAKE_RELOAD_FAIL:-0} == 0 ]] || exit 1
        # Model the manager loading the on-disk graph only after daemon-reload.
        if [[ -f "$TEST_DROPIN" ]]; then
            cp "$TEST_DROPIN" "$TEST_STATE"
        else
            : > "$TEST_STATE"
        fi
        ;;
    'show miningcore.service -p Wants -p After --no-pager')
        [[ ${FAKE_SHOW_FAIL:-0} == 0 ]] || exit 1
        case "${FAKE_PROPERTIES:-valid}" in
            valid) cat "$TEST_STATE" ;;
            missing-wants) echo 'Wants=network.target'; echo 'After=postgresql@17-main.service' ;;
            missing-after) echo 'Wants=postgresql@17-main.service'; echo 'After=network.target' ;;
            substring) echo 'Wants=postgresql@17-main.service.extra'; echo 'After=postgresql@17-main.service' ;;
            empty) printf 'Wants=\nAfter=\n' ;;
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
    if [[ $expected -ne 0 ]] && grep -Fq 'Configured Miningcore ordering' <<< "$output"; then
        echo 'Failure incorrectly claimed successful configuration' >&2
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
expect 69 'does not exist' bash "$helper" --dry-run --unit postgresql@99-missing.service
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
expect 69 'Install miningcore.service' env FAKE_MININGCORE_STATUS=1 bash "$helper" --dry-run
expect 69 'Install miningcore.service' env FAKE_MININGCORE_STATUS=1 bash "$helper"
[[ ! -e "$dropin_dir" ]]
if grep -Eq 'daemon-reload|^show ' "$TEST_TRACE"; then exit 1; fi

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
printf '[Service]\nRestart=no\n' > "$dropin_dir/operator.conf"
cp "$dropin_dir/operator.conf" "$fixture_dir/operator.expected"
: > "$TEST_TRACE"
expect 78 'existing drop-in remains' env FAKE_PG_UNITS=none bash "$helper"
expect 78 'existing drop-in remains' env FAKE_PG_UNITS=none bash "$helper" --dry-run
expect 78 'refusing to guess' env FAKE_PG_UNITS=many bash "$helper"
expect 69 'Unable to query systemd' env FAKE_PG_UNITS=fail bash "$helper"
cmp "$fixture_dir/expected" "$dropin"
if grep -Eq 'daemon-reload|^show ' "$TEST_TRACE"; then exit 1; fi
expect 0 'Would remove' bash "$helper" --remove --dry-run
cmp "$fixture_dir/expected" "$dropin"
expect 0 'Configured Miningcore ordering' env FAKE_PG_UNITS=new bash "$helper"
grep -Fxq 'After=postgresql@18-main.service' "$dropin"
if grep -Fq '17-main' "$dropin"; then exit 1; fi
expect 0 'Configured Miningcore ordering' env FAKE_PG_UNITS=many bash "$helper" --unit postgresql@17-main.service
cmp "$fixture_dir/expected" "$dropin"

expect 69 'Unable to reload systemd' env FAKE_RELOAD_FAIL=1 bash "$helper"
expect 69 'Unable to verify' env FAKE_SHOW_FAIL=1 bash "$helper"
for properties in missing-wants missing-after substring empty; do
    expect 78 'Verification failed' env FAKE_PROPERTIES="$properties" bash "$helper"
done

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
[[ ! -s "$TEST_STATE" ]]
cmp "$fixture_dir/operator.expected" "$dropin_dir/operator.conf"
expect 0 'Removed managed PostgreSQL ordering' bash "$helper" --remove
expect 0 'Skipping Miningcore/PostgreSQL ordering' env FAKE_PG_UNITS=none bash "$helper"

ln -s "$fixture_dir/expected" "$dropin"
expect 78 'unsafe managed drop-in path' bash "$helper"
expect 78 'unsafe managed drop-in path' bash "$helper" --remove
cmp "$fixture_dir/expected" "$dropin"

echo 'systemd PostgreSQL ordering helper tests passed'
