#!/usr/bin/env bash
set -euo pipefail

instance="${1:?usage: run-rpc-fault-proxy.sh litecoin|dogecoin}"
gateway="$(ip route show default | awk 'NR == 1 { print $3 }')"
log_dir="/var/log/miningcore-regtest"

case "$instance" in
    litecoin)
        listen_port=20332
        upstream_port=19342
        control=/tmp/ltc-fault.json
        log_file="$log_dir/ltc-rpc-proxy.jsonl"
        ;;
    dogecoin)
        listen_port=45555
        upstream_port=44565
        control=/tmp/doge-fault.json
        log_file="$log_dir/doge-rpc-proxy.jsonl"
        ;;
    *)
        echo "unsupported proxy instance: $instance" >&2
        exit 64
        ;;
esac

if [[ -z "$gateway" ]]; then
    echo "unable to determine the Windows WSL gateway" >&2
    exit 1
fi

mkdir -p "$log_dir"

exec /usr/bin/python3 /usr/local/lib/miningcore-regtest/rpc_fault_proxy.py \
    --listen-host 127.0.0.1 \
    --listen-port "$listen_port" \
    --upstream "http://${gateway}:${upstream_port}/" \
    --control "$control" \
    --log "$log_file"
