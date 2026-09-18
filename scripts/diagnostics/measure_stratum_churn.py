#!/usr/bin/env python3
"""Measure the actual BLAKE2b listener in a separate .NET test process.

Build Release tests first. Uses loopback, supplied jobs and no daemon/database.
No credentials, production services or existing pool endpoints are used.
"""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[2]


def measure(enforce):
    with tempfile.TemporaryDirectory(prefix="miningcore-churn-") as temp:
        prefix = Path(temp) / "measurement"
        env = os.environ.copy()
        env["MININGCORE_TEST_CHURN_MEASUREMENT"] = str(prefix)
        env["MININGCORE_TEST_CHURN_ENFORCE"] = str(enforce).lower()
        command = ["dotnet", "test", str(ROOT / "src/Miningcore.Tests/Miningcore.Tests.csproj"),
                   "-c", "Release", "--no-build", "--filter",
                   "FullyQualifiedName~RealListener_ReconnectChurn"]
        with open(Path(temp) / "host.log", "w+") as log:
            process = subprocess.Popen(command, cwd=ROOT, env=env, stdout=log,
                                       stderr=subprocess.STDOUT,
                                       creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            try:
                ready = Path(str(prefix) + ".ready")
                deadline = time.monotonic() + 60
                while not ready.exists():
                    if process.poll() is not None or time.monotonic() > deadline:
                        raise RuntimeError("Measurement host did not start")
                    time.sleep(.025)
                port = int(ready.read_text())
                admitted = 0
                for _ in range(256):
                    subscribed = False
                    try:
                        with socket.create_connection(("127.0.0.1", port), timeout=15) as client:
                            with client.makefile("rb") as reader:
                                def request(request_id, method, params):
                                    client.sendall((json.dumps(dict(id=request_id, method=method, params=params)) + "\n").encode())

                                def receive():
                                    line = reader.readline()
                                    return json.loads(line) if line else None

                                request(1, "mining.subscribe", ["churn-measurement"])
                                reply = receive()
                                if reply is None:
                                    if not enforce:
                                        raise AssertionError("Generous policy refused startup")
                                    continue
                                assert reply["id"] == 1 and not reply.get("error"), reply
                                subscribed = True
                                assert receive()["method"] == "mining.set_difficulty"
                                assert receive()["method"] == "mining.notify"
                                for j in range(8):
                                    request(j + 2, "mining.suggest_difficulty", [(j + 2) / 1e9])
                                    assert receive()["result"] is True
                                    assert receive()["method"] == "mining.set_difficulty"
                                    assert receive()["method"] == "mining.notify"
                                admitted += 1
                    except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
                        if not enforce or subscribed:
                            raise
                assert admitted == (8 if enforce else 256), admitted
                Path(str(prefix) + ".stop").write_text("done")
                if process.wait(timeout=30):
                    raise RuntimeError("Measurement host assertions failed")
                result = json.loads(Path(str(prefix) + ".result.json").read_text())
                result["admitted"] = admitted
                return result
            except Exception:
                log.flush()
                log.seek(0)
                print(log.read())
                raise
            finally:
                Path(str(prefix) + ".stop").write_text("stop")
                if process.poll() is None:
                    try:
                        process.wait(timeout=30)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()


if __name__ == "__main__":
    print(json.dumps([measure(False), measure(True)], indent=2))
