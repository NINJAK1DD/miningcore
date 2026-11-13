#!/usr/bin/env bash
# Run after the normal build, without other lab workloads; keep raw host metrics.
set -euo pipefail
umask 077
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$repo"
for tool in pidstat iostat lscpu; do
  command -v "$tool" >/dev/null || { echo "Missing $tool (install sysstat/util-linux)" >&2; exit 1; }
done
evidence=$(mktemp -d "$repo/build/test-lab/pps-capacity-XXXXXXXX")
printf 'Capacity evidence: %s\n' "$evidence"
lscpu > "$evidence/cpu.txt"
uname -a > "$evidence/kernel.txt"
df -T "$repo" > "$evidence/filesystem.txt"
git rev-parse HEAD > "$evidence/commit.txt"
export LC_ALL=C S_TIME_FORMAT=ISO
pidstat -h -u -d -C postgres 1 > "$evidence/postgres-processes.txt" &
pg_sampler=$!
iostat -d -x -y 1 > "$evidence/device-io.txt" &
io_sampler=$!
cleanup() {
  kill "$pg_sampler" "$io_sampler" 2>/dev/null || true
  wait "$pg_sampler" "$io_sampler" 2>/dev/null || true
}
trap cleanup EXIT
bash scripts/test-lab/with-postgres.sh dotnet test \
  src/Miningcore.Tests/Miningcore.Tests.csproj --no-build --no-restore \
  --filter 'FullyQualifiedName~PpsCapacity_Sustains1000|FullyQualifiedName~PpsRelayCapacity_Sustains1000' \
  --logger 'trx;LogFileName=pps-capacity.trx' --results-directory "$evidence" \
  > "$evidence/tests.log" 2>&1
cat "$evidence/tests.log"
