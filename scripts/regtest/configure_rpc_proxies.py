#!/usr/bin/env python3
"""Point LTC and DOGE daemon endpoints in a Miningcore config at local proxies."""

import argparse
import json
import os
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("config", type=Path)
    parser.add_argument("--litecoin-port", type=int, required=True)
    parser.add_argument("--dogecoin-port", type=int, required=True)
    args = parser.parse_args()

    document = json.loads(args.config.read_text(encoding="utf-8-sig"))
    updated = set()

    for pool in document.get("pools", []):
        coin = str(pool.get("coin", "")).lower()
        if coin == "litecoin":
            port = args.litecoin_port
        elif coin == "dogecoin":
            port = args.dogecoin_port
        else:
            continue

        for daemon in pool.get("daemons", []):
            daemon["host"] = "127.0.0.1"
            daemon["port"] = port
        updated.add(coin)

    if updated != {"litecoin", "dogecoin"}:
        raise SystemExit(f"Expected Litecoin and Dogecoin pools, updated: {sorted(updated)}")

    temporary = args.config.with_suffix(args.config.suffix + ".tmp")
    temporary.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, args.config)


if __name__ == "__main__":
    main()
