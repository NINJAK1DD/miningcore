#!/usr/bin/env python3
"""Linux lab gate: prove the maximum admitted fixture and measure VmPeak, not only RSS."""
import argparse
import concurrent.futures
import gzip
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument("worker", type=Path)
parser.add_argument("--runs", type=int, default=3)
args = parser.parse_args()
assert 1 <= args.runs <= 10
worker = args.worker.resolve(strict=True)
fixtures = Path(__file__).resolve().parents[2] / "src/Miningcore.Tests/Fixtures/Pearl"
meta = json.loads((fixtures / "srb-proof.json").read_text())
proof = gzip.decompress((fixtures / meta["proofFile"]).read_bytes())
assert hashlib.sha256(proof).hexdigest() == meta["proofSha256"]
assert struct.unpack_from("<QQQQ", proof) == (32772, 15360, 32768, 128)
request = (b"PRLJOB02\x01\x03\0\0" + bytes.fromhex(meta["header"]) +
           int(meta["target"], 16).to_bytes(32, "big") + struct.pack("<I", len(proof)) + proof)
measurements = []
for _ in range(args.runs):
    process = subprocess.Popen([str(worker)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, env={"LANG": "C", "RAYON_NUM_THREADS": "4", "MALLOC_ARENA_MAX": "2"})
    peak = rss = 0
    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
        result = executor.submit(process.communicate, request)
        try:
            while process.poll() is None:
                if time.monotonic() - started > 180:
                    raise TimeoutError("Prover exceeded certified deadline")
                try:
                    values = {line.split(":", 1)[0]: line.split(":", 1)[1].strip()
                              for line in Path(f"/proc/{process.pid}/status").read_text().splitlines()}
                    peak = max(peak, int(values.get("VmPeak", "0 kB").split()[0]))
                    rss = max(rss, int(values.get("VmHWM", "0 kB").split()[0]))
                except FileNotFoundError:
                    pass
                time.sleep(.01)
        finally:
            if process.poll() is None:
                process.kill()
        output, errors = result.result(timeout=5)
    assert process.returncode == 0 and not errors, "Certificate generation failed"
    assert output[:12] == b"PRLRES02\0\x01\0\0"
    assert output[12:176] == bytes.fromhex(meta["publicData"])
    size = struct.unpack_from("<I", output, 176)[0]
    assert 0 < size <= 60000 and len(output) == 180 + size
    assert 0 < peak <= 10 * 1024 * 1024, "Prover lacks 2 GiB of virtual-address headroom"
    measurements.append({"vmPeakKiB": peak, "rssPeakKiB": rss, "seconds": round(time.monotonic() - started, 3)})
print(json.dumps({"workerSha256": hashlib.sha256(worker.read_bytes()).hexdigest(),
                  "limitKiB": 12 * 1024 * 1024, "maximumVmPeakKiB": 10 * 1024 * 1024,
                  "runs": measurements}, indent=2))
