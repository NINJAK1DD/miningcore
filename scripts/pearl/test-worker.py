#!/usr/bin/env python3
"""Executable contract tests for the isolated worker; no daemon, GPU or wallet required."""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("worker", type=Path)
parser.add_argument("--prove", action="store_true", help="also run the slower bounded certificate path")
args = parser.parse_args()
worker = args.worker.resolve(strict=True)
fixtures = Path(__file__).resolve().parents[2] / "src/Miningcore.Tests/Fixtures/Pearl"
env = dict(os.environ, RAYON_NUM_THREADS="4")
version = subprocess.run([str(worker), "--version"], check=True, capture_output=True, timeout=5).stdout.decode().strip()
assert version == "miningcore-pearl-worker 2 d7c517d267f10f5bcbbdc366f3e603942f9a5ab5 linux-x64"

cases = 0
def run(name, request, valid, candidate=False, public=None, prove=False):
    global cases
    result = subprocess.run([str(worker)], input=request, capture_output=True, timeout=180 if prove else 5, env=env)
    assert result.returncode == (0 if valid else 1), (name, "exit", result.returncode)
    assert result.stdout[:8] == b"PRLRES02", (name, "magic")
    if valid:
        assert result.stdout[8:12] == bytes([0, int(candidate), 0, 0]), (name, "status")
        if public is not None:
            assert result.stdout[12:176] == public, (name, "public data")
        length = struct.unpack_from("<I", result.stdout, 176)[0]
        assert len(result.stdout) == 180 + length
        assert (0 < length <= 60000) if prove else length == 0
    else:
        assert result.stdout == b"PRLRES02\x01\0\0\0", (name, "error frame")
    assert not result.stderr, (name, "unexpected diagnostic output")
    cases += 1
    print("PASS", name, flush=True)

for fixture in ("srb", "share-only"):
    meta = json.loads((fixtures / (fixture + "-proof.json")).read_text())
    raw = gzip.decompress((fixtures / meta["proofFile"]).read_bytes())
    assert hashlib.sha256(raw).hexdigest() == meta["proofSha256"]
    public = bytes.fromhex(meta["publicData"])
    target = int(meta["target"], 16)
    def packet(target=target, operation=0, proof=raw):
        return (b"PRLJOB02" + bytes([operation, 3, 0, 0]) + bytes.fromhex(meta["header"]) +
                target.to_bytes(32, "big") + struct.pack("<I", len(proof)) + proof)
    request = packet()
    run(fixture + ":valid", request, True, meta["candidate"], public)
    jackpot = int.from_bytes(public[116:148], "little")
    minimum = (jackpot + meta["workFactor"] - 1) // meta["workFactor"]
    run(fixture + ":bound-1", packet(minimum - 1), False)
    run(fixture + ":bound", packet(minimum), True, meta["candidate"], public)
    run(fixture + ":bound+1", packet(minimum + 1), True, meta["candidate"], public)
    run(fixture + ":zero-target", packet(0), False)
    run(fixture + ":overflowing-target", packet((1 << 256) - 1), False)
    run(fixture + ":wrong-header", request[:48] + bytes([request[48] ^ 1]) + request[49:], False)
    run(fixture + ":unknown-version", request[:9] + b"\x04" + request[10:], False)
    run(fixture + ":truncated", request[:-17], False)
    run(fixture + ":trailing", request + b"\0", False)
    run(fixture + ":huge-nested-count", packet(proof=raw[:32] + b"\xff" * 8 + raw[40:]), False)
    run(fixture + ":changed-leaf", packet(proof=raw[:48] + bytes([raw[48] ^ 1]) + raw[49:]), False)
    if not meta["candidate"]:
        run(fixture + ":cannot-prove-share-only", packet(operation=1), False)
    elif args.prove:
        run(fixture + ":certificate", packet(operation=1), True, True, public, True)
print(f"{cases} worker contract cases passed.")
