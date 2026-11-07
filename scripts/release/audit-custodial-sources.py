#!/usr/bin/env python3
"""Locate custodial RPC source fields. This is NOT daemon compatibility certification."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
FAMILIES = {"bitcoin", "progpow", "satoshicash", "equihash", "nexa"}
WALLET_PATHS = ["src/wallet/rpc/transactions.cpp", "src/wallet/rpcwallet.cpp",
                "src/rpcwallet.cpp", "src/rpc/wallet.cpp"]
BLOCK_PATHS = ["src/rpc/blockchain.cpp", "src/rpcblockchain.cpp"]


def audit(name, coin, discovery):
    repository = discovery.get("repository") if discovery else coin.get("github")
    row = {
        "template": name, "family": coin["family"],
        "handler": "EquihashPayoutHandler (inherited classification)"
            if coin["family"] == "equihash" and not coin.get("useBitcoinPayoutHandler")
            else "BitcoinPayoutHandler",
        "template_repository": coin.get("github") or None,
        "repository": repository or None,
    }
    if discovery:
        row["research"] = {k: v for k, v in discovery.items() if k != "template"}
    if not repository:
        row["status"] = "Authoritative matching source unresolved"
        return row
    repository = repository.rstrip("/").removesuffix(".git")
    parsed = urllib.parse.urlparse(repository)
    if parsed.scheme != "https" or parsed.hostname not in {"github.com", "gitlab.com"}:
        row["status"] = "Repository URL requires manual inspection"
        return row
    try:
        result = subprocess.run(
            ["git", "-c", "credential.helper=", "ls-remote", repository + ".git", "HEAD"],
            cwd=tempfile.gettempdir(), capture_output=True, text=True, timeout=30,
            env={**os.environ, "GIT_TERMINAL_PROMPT": "0"})
        values = result.stdout.split()
        if result.returncode or not values or not re.fullmatch(r"[a-f0-9]{40}", values[0]):
            row["status"] = "Upstream revision unavailable"
            return row
        sha = row["revision"] = values[0]
        prefix = ("https://raw.githubusercontent.com/" + parsed.path.strip("/") + "/" + sha + "/"
                  if parsed.hostname == "github.com" else repository + "/-/raw/" + sha + "/")

        def fetch(paths):
            for path in paths:
                if name == "tdcoin":
                    path = "tdcoin-0.18.0/" + path
                try:
                    with urllib.request.urlopen(prefix + path, timeout=15) as response:
                        data = response.read(2_000_001)
                    if len(data) > 2_000_000:
                        continue
                    return {"url": prefix + path, "sha256": hashlib.sha256(data).hexdigest()}, data.decode()
                except (OSError, UnicodeError):
                    continue
            return None, ""

        for kind, paths, fields in [
            ("wallet", WALLET_PATHS, ["confirmations", "generated", "blockindex", "blockhash",
                                     "txid", "immature", "generate"]),
            ("block", BLOCK_PATHS, ["hash", "height", "confirmations",
                                   "txid" if coin["family"] == "nexa" else "tx"]),
        ]:
            source, content = fetch(paths)
            if not source:
                continue
            # BSV moved WalletTxToJSON/ListTransactions into the wallet implementation.
            if name == "bitcoin-sv" and kind == "wallet":
                supplemental, extra = fetch(["src/wallet/wallet.cpp"])
                if supplemental:
                    source["supplemental"] = supplemental
                    content += "\n" + extra
            source["fields_found"] = [field for field in fields if '"' + field + '"' in content]
            source["missing_fields"] = [field for field in fields if field not in source["fields_found"]]
            row[kind] = source
        row["status"] = ("Source fields located; not live daemon certification"
                         if all(k in row and not row[k]["missing_fields"] for k in ("wallet", "block"))
                         else "Contract requires manual inspection")
        if discovery and discovery.get("authority") == "candidate":
            row["status"] = "Source candidate; upstream ownership requires confirmation"
    except (OSError, subprocess.TimeoutExpired):
        row["status"] = "Upstream source unavailable"
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "build/custodial-source-audit/inventory.json")
    args = parser.parse_args()
    coins = json.loads((ROOT / "src/Miningcore/coins.json").read_text(encoding="utf-8"))
    research = json.loads((ROOT / "docs/custodial-repository-research.json").read_text(encoding="utf-8"))
    discoveries = {row["template"]: row for row in research["entries"]}
    items = sorted((name, coin) for name, coin in coins.items() if coin.get("family") in FAMILIES)
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        rows = list(pool.map(lambda item: audit(*item, discoveries.get(item[0])), items))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # One template per line keeps the reviewable inventory compact.
    args.output.write_text("[\n" + ",\n".join("  " + json.dumps(row, sort_keys=True) for row in rows) + "\n]\n",
                           encoding="utf-8")
    for status in sorted({row["status"] for row in rows}):
        print(f"{sum(row['status'] == status for row in rows)}: {status}")
    print(f"Total: {len(rows)}; output: {args.output}")


if __name__ == "__main__":
    main()
