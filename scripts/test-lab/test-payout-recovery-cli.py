"""Exercise the real CLI through a pseudo-terminal against the isolated lab database."""
import errno
import json
import os
import pty
import select
import signal
import subprocess
import sys
import time
import uuid
from pathlib import Path

app, scratch = sys.argv[1:]
config = Path(scratch) / "recovery.json"
config.write_text(json.dumps({"persistence": {"postgres": {
    "host": os.environ["PGHOST"], "port": int(os.environ["PGPORT"]),
    "user": os.environ["PGUSER"], "database": os.environ["PGDATABASE"],
}}, "pools": []}))
command = ["dotnet", app, "-c", str(config), "--recover-payout-ownership"]


def sql(query):
    return subprocess.check_output(["psql", "-X", "-At", "-v", "ON_ERROR_STOP=1", "-c", query], text=True).strip()


def seed():
    owner = str(uuid.uuid4())
    sql("DELETE FROM payout_manager_ownership; "
        "INSERT INTO payout_manager_ownership VALUES "
        f"(1, 7, '{owner}', 'lab-host', 123, now(), NULL)")
    return owner


def terminal(steps, expected):
    master, slave = pty.openpty()
    process = subprocess.Popen(command, stdin=slave, stdout=slave, stderr=slave)
    os.close(slave)
    transcript = ""
    deadline = time.monotonic() + 30
    try:
        while time.monotonic() < deadline:
            if select.select([master], [], [], 0.1)[0]:
                try:
                    chunk = os.read(master, 65536)
                except OSError as error:
                    if error.errno != errno.EIO:
                        raise
                    break
                if not chunk:
                    break
                transcript += chunk.decode(errors="replace")
            if steps and steps[0][0] in transcript:
                _, answer = steps.pop(0)
                if answer is None:
                    process.send_signal(signal.SIGINT)
                else:
                    os.write(master, answer.encode())
        assert process.wait(timeout=5) == expected, transcript
        assert not steps, transcript
        return transcript
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        os.close(master)


owner = seed()
result = subprocess.run(command, input=f"3\nSTOPPED\nCHECKED\nRELEASE {owner}\n", text=True, capture_output=True, timeout=30)
assert result.returncode == 78, result.stdout + result.stderr
assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
print("PASS: redirected CLI input cannot clear ownership")

terminal([("Choice [1]:", "\n")], 78)
assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
print("PASS: default terminal choice preserves ownership")

terminal([("Choice [1]:", None)], 78)
assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
print("PASS: Ctrl+C exits the terminal assistant without clearing ownership")

output = terminal([
    ("Choice [1]:", "3\n"),
    ("Type STOPPED", "STOPPED\n"),
    ("Type CHECKED", "CHECKED\n"),
    (f"type RELEASE {owner}:", f"RELEASE {owner}\n"),
], 0)
assert "Released ownership generation 7" in output, output
assert sql("SELECT owner_id IS NULL FROM payout_manager_ownership") == "t"
print("PASS: explicit terminal recovery clears only the inspected owner and exits successfully")
