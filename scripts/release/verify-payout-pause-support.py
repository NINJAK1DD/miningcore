#!/usr/bin/env python3
"""Fail closed before starting the exact selected apphost with a paused config.

This checks capability/configuration, not binary authenticity or remote node state.
It never starts mining, connects to wallets, or changes payout ownership.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile


def validate_schema(schema):
    payment = schema["definitions"]["ClusterPaymentProcessingConfig"]
    pause = payment["properties"]["paused"]
    if not isinstance(pause, dict) or pause.get("type") != "boolean" or pause.get("default") is not False:
        raise ValueError("binary does not declare the supported payout pause")
    if payment.get("additionalProperties") is not False:
        raise ValueError("binary does not reject unknown cluster payment controls")

def validate(schema, diagnostic):
    validate_schema(schema)
    state = diagnostic["configuration"]["paymentProcessing"]
    if not isinstance(state, dict) or state.get("paused") is not True or state.get("enabled") is not True:
        raise ValueError("selected configuration is not explicitly enabled and paused")


def verify(binary, config, run=subprocess.run):
    # Resolve an exact apphost, never a PATH lookup or a shell command.
    binary = Path(binary).resolve(strict=True)
    config = Path(config).resolve(strict=True)
    installed_schema = binary.parent / "config.schema.json"
    paths = {"apphost": binary, "configuration": config, "installed_schema": installed_schema}
    def fingerprints():
        return {key: {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                for key, path in paths.items()}
    before = fingerprints()
    installed = json.loads(installed_schema.read_text(encoding="utf-8-sig"))
    validate_schema(installed)
    with tempfile.TemporaryDirectory(prefix="miningcore-pause-") as directory:
        schema_path = Path(directory) / "schema.json"
        def invoke(arguments):
            return run([str(binary), *arguments], check=True, capture_output=True,
                       text=True, timeout=60).stdout
        invoke(["--generate-config-schema", str(schema_path)])
        schema = json.loads(schema_path.read_text(encoding="utf-8-sig"))
        validate_schema(schema)
        if schema != installed:
            raise ValueError("installed schema differs from the selected binary's generated schema")
        diagnostic = json.loads(invoke(["--dumpconfig", "-c", str(config)]))
        validate(schema, diagnostic)
    if fingerprints() != before:
        raise ValueError("deployment files changed during verification")
    return before


def write_receipt(path, fingerprints):
    # Private, exclusive creation: never overwrite configuration or existing evidence.
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w", encoding="utf-8") as output:
        json.dump({"format": 1, "checked_files": fingerprints}, output, indent=2)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", required=True, help="exact service apphost path")
    parser.add_argument("--config", required=True, help="exact service configuration path")
    parser.add_argument("--receipt", help="new private JSON evidence file; does not bind a later launch")
    parser.add_argument("--all-payout-nodes-stopped", action="store_true",
                        help="operator attests all wallet-capable processes were stopped")
    args = parser.parse_args()
    if not args.all_payout_nodes_stopped:
        parser.error("confirm all payout-capable processes are stopped before a paused rollout")
    try:
        fingerprints = verify(args.binary, args.config)
        if args.receipt:
            write_receipt(args.receipt, fingerprints)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        # Do not replay CLI diagnostics that might contain configuration values.
        print("Payout pause pre-start check FAILED. Keep services stopped; inspect binary and config privately.")
        return 78
    print("Payout pause supported and explicitly configured. Keep other nodes stopped until individually checked.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
