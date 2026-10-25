#!/usr/bin/env bash
# An opt-in, isolated live test. Never point this harness at a public network.
set -euo pipefail
[[ $(id -u) != 0 ]] || { echo 'Run as an unprivileged Docker-capable lab user' >&2; exit 1; }
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
source_dir="$repo/scripts/tests/alephium-devnet"
output=${MININGCORE_OUTPUT:-"$repo/src/Miningcore/bin/Debug/net10.0"}
output=$(realpath -- "$output")
[[ -f "$output/Miningcore.dll" && -f "$output/libmultihash.so" ]] || { echo 'Build Miningcore with Linux native libraries first' >&2; exit 1; }
evidence=${1:-"$repo/build/alephium-devnet"}
mkdir -p -- "$evidence"
evidence=$(realpath -- "$evidence")
task=$(mktemp -d "${TMPDIR:-/tmp}/miningcore-alph.XXXXXXXX")
container="miningcore-alph-${task##*.}"
network="$container"
cleanup() {
    local status=$?
    trap - EXIT
    # Capture diagnostics before removing the node, including when dotnet/tee
    # fails under errexit + pipefail. Cleanup must preserve the original status.
    docker logs "$container" > "$evidence/node.log" 2>&1 || true
    docker rm --force --volumes "$container" >/dev/null 2>&1 || true
    docker network rm "$network" >/dev/null 2>&1 || true
    # Only remove the private directory allocated by mktemp for this run.
    rm -rf -- "$task"
    exit "$status"
}
trap cleanup EXIT
image='alephium/alephium@sha256:c8bc624a166f7328c244f975d9fe5581609fe5a66f09533ff7115b39ffa80351'
docker pull "$image" > "$evidence/image.log"
dotnet build "$source_dir/AlephiumDevnet.csproj" -p:MiningcoreOutput="$output" -o "$task/out" > "$evidence/build.log" 2>&1 || { tail -n 40 "$evidence/build.log"; exit 1; }
cp -- "$output"/*.so "$task/out/"
docker network create --internal "$network" > /dev/null
# No published ports, bootstrap peers, UPnP, wallet or public-network traffic.
docker run -d --name "$container" --network "$network" --security-opt no-new-privileges --cap-drop ALL \
    --memory 2g --cpus 2 -e JAVA_MEM_OPTS='-Xms256M -Xmx1400M' \
    --mount "type=bind,src=$source_dir/devnet.conf,dst=/alephium-home/.alephium/user.conf,readonly" \
    "$image" > "$evidence/container-id.txt"
node_ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$container")
[[ -n "$node_ip" ]] || { echo 'No private container address' >&2; exit 1; }
ready=false
for attempt in {1..45}; do
    status=$(curl --max-time 2 -fsS "http://$node_ip:22973/infos/self-clique" 2>/dev/null || true)
    if [[ "$status" == *'"synced":true'* ]]; then ready=true; break; fi
    sleep 1
done
if [[ "$ready" != true ]]; then echo 'Devnet failed to become ready' >&2; exit 1; fi
sha256sum "$output/Miningcore.dll" > "$evidence/miningcore-sha256.txt"
LD_LIBRARY_PATH="$output${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    dotnet "$task/out/AlephiumDevnet.dll" "$node_ip" "$evidence/result.json" 2>&1 | tee "$evidence/run.log"
