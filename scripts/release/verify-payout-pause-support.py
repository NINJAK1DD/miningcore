#!/usr/bin/env python3
"""Fail closed before starting the exact selected apphost with a paused config.

This checks capability/configuration, not binary authenticity or remote node state.
It never starts mining, connects to wallets, or changes payout ownership.
"""
import argparse
import json
from pathlib import Path
import subprocess
import tempfile


def validate(schema, diagnostic):
    payment = schema["definitions"]["ClusterPaymentProcessingConfig"]
    pause = payment["properties"]["paused"]
    if not isinstance(pause, dict) or pause.get("type") != "boolean" or pause.get("default") is not False:
        raise ValueError("binary does not declare the supported payout pause")
    if payment.get("additionalProperties") is not False:
        raise ValueError("binary does not reject unknown cluster payment controls")
    state = diagnostic["configuration"]["paymentProcessing"]
    if state.get("paused") is not True or state.get("enabled") is not True:
        raise ValueError("selected configuration is not explicitly enabled and paused")


def verify(binary, config, run=subprocess.run):
    # Resolve an exact apphost, never a PATH lookup or a shell command.
    binary = Path(binary).resolve(strict=True)
    config = Path(config).resolve(strict=True)
    with tempfile.TemporaryDirectory(prefix="miningcore-pause-") as directory:
        schema_path = Path(directory) / "schema.json"
        def invoke(arguments):
            return run([str(binary), *arguments], check=True, capture_output=True,
                       text=True, timeout=60).stdout
        invoke(["--generate-config-schema", str(schema_path)])
        schema = json.loads(schema_path.read_text(encoding="utf-8-sig"))
        diagnostic = json.loads(invoke(["--dumpconfig", "-c", str(config)]))
        validate(schema, diagnostic)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, help="exact service apphost path")
    parser.add_argument("--config", required=True, help="exact service configuration path")
    parser.add_argument("--all-payout-nodes-stopped", action="store_true",
                        help="operator attests all wallet-capable processes were stopped")
    args = parser.parse_args()
    if not args.all_payout_nodes_stopped:
        parser.error("confirm all payout-capable processes are stopped before a paused rollout")
    try:
        verify(args.binary, args.config)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        # Do not replay CLI diagnostics that might contain configuration values.
        print("Payout pause pre-start check FAILED. Keep services stopped; inspect binary and config privately.")
        return 78
    print("Payout pause supported and explicitly configured. Keep other nodes stopped until individually checked.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
