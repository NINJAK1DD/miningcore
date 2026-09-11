#!/usr/bin/env bash

set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
helper="$repository_root/packaging/systemd/configure-postgresql-ordering.sh"
fixture_dir=$(mktemp -d)
trap 'rm -rf -- "$fixture_dir"' EXIT
mkdir -p "$fixture_dir/bin"

cat > "$fixture_dir/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "${1:-}" in
  list-units)
    case "${FAKE_PG_UNITS:-}" in
      none) ;;
      one) printf '%s loaded active running PostgreSQL Cluster\n' 'postgresql@17-main.service' ;;
      many)
        printf '%s loaded active running PostgreSQL Cluster\n' 'postgresql@16-old.service'
        printf '%s loaded active running PostgreSQL Cluster\n' 'postgresql@17-main.service'
        ;;
      *) exit 2 ;;
    esac
    ;;
  cat)
    case "${2:-}" in
      postgresql@16-old.service|postgresql@17-main.service) exit 0 ;;
      *) exit 1 ;;
    esac
    ;;
  *)
    echo "unexpected systemctl command: $*" >&2
    exit 2
    ;;
esac
EOF
chmod +x "$fixture_dir/bin/systemctl"

run_helper() {
  PATH="$fixture_dir/bin:$PATH" "$helper" --dry-run "$@"
}

output=$(FAKE_PG_UNITS=one run_helper)
grep -Fq 'Detected local PostgreSQL cluster: postgresql@17-main.service' <<<"$output"
grep -Fq 'Wants=postgresql@17-main.service' <<<"$output"
grep -Fq 'After=postgresql@17-main.service' <<<"$output"

output=$(FAKE_PG_UNITS=none run_helper)
grep -Fq 'No running local postgresql@*.service cluster was detected.' <<<"$output"
grep -Fq 'Skipping Miningcore/PostgreSQL ordering' <<<"$output"

set +e
output=$(FAKE_PG_UNITS=many run_helper 2>&1)
status=$?
set -e
[[ $status -eq 78 ]]
grep -Fq 'Multiple running local PostgreSQL clusters were detected; refusing to guess:' <<<"$output"
grep -Fq 'postgresql@16-old.service' <<<"$output"
grep -Fq 'postgresql@17-main.service' <<<"$output"

output=$(FAKE_PG_UNITS=many run_helper --unit postgresql@17-main.service)
grep -Fq 'Wants=postgresql@17-main.service' <<<"$output"
grep -Fq 'After=postgresql@17-main.service' <<<"$output"

set +e
output=$(FAKE_PG_UNITS=one run_helper --unit ssh.service 2>&1)
status=$?
set -e
[[ $status -eq 64 ]]
grep -Fq 'Refusing invalid PostgreSQL cluster unit: ssh.service' <<<"$output"

echo 'systemd PostgreSQL ordering helper tests passed'
