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

app, scratch, test_host = sys.argv[1:]
assert os.geteuid() != 0, "The CLI driver must run as the unprivileged lab account"
config = Path(scratch) / "recovery.json"
config.write_text(json.dumps({"persistence": {"postgres": {
    "host": os.environ["PGHOST"], "port": int(os.environ["PGPORT"]),
    "user": os.environ["PGUSER"], "database": os.environ["PGDATABASE"],
    "password": Path(os.environ["PGPASSFILE"]).read_text().strip().split(":")[-1],
}}, "pools": []}))
config.chmod(0o600)
command = ["dotnet", app, "-c", str(config), "--recover-payout-ownership"]


def sql(query):
    return subprocess.check_output(["psql", "-X", "-At", "-v", "ON_ERROR_STOP=1", "-c", query], text=True).strip()


def seed():
    owner = str(uuid.uuid4())
    sql("DELETE FROM payout_manager_ownership; "
        "INSERT INTO payout_manager_ownership VALUES "
        f"(1, 7, '{owner}', 'lab-host', 123, now(), NULL)")
    return owner


def terminal(steps, expected, timeout=30):
    master, slave = pty.openpty()
    process = subprocess.Popen(command, stdin=slave, stdout=slave, stderr=slave)
    uid_line = next(line for line in Path(f"/proc/{process.pid}/status").read_text().splitlines() if line.startswith("Uid:"))
    assert all(int(uid) == os.geteuid() for uid in uid_line.split()[1:]), uid_line
    os.close(slave)
    transcript = ""
    deadline = time.monotonic() + timeout
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
        try:
            status = process.wait(timeout=5)
        except subprocess.TimeoutExpired as error:
            raise AssertionError(f"CLI timed out; pending prompts: {steps!r}\n{transcript}") from error
        assert status == expected, transcript
        assert not steps, transcript
        return transcript
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        os.close(master)


sql("ALTER TABLE payout_manager_ownership RENAME TO saved_ownership")
try:
    result = subprocess.run(command, input="", text=True, capture_output=True, timeout=30)
    assert result.returncode == 1, result.stdout + result.stderr
    assert "add_payout_manager_ownership.sql" in result.stdout + result.stderr
    assert "Sequence contains no elements" not in result.stdout + result.stderr
finally:
    sql("ALTER TABLE saved_ownership RENAME TO payout_manager_ownership")
print("PASS: Main reports actionable missing-ownership migration guidance")

result = subprocess.run(command + ["--no-payout-recovery-prompt"], input="", capture_output=True, text=True, timeout=30)
assert result.returncode == 1 and "on its own" in result.stdout + result.stderr
print("PASS: contradictory recovery flags are rejected before database access")
audit_schema = sql("SELECT 'miningcore_audit_' || 'payout_manager_ownership'::regclass::oid::text")
audit_table = audit_schema + ".payout_manager_recovery_events"
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

output = terminal([], 78, timeout=150)
assert "timed out" in output, output
assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
print("PASS: an unattended real terminal times out and preserves ownership")

output = terminal([
    ("Choice [1]:", "3\n"),
    ("Type STOPPED", "STOPPED\n"),
    ("Type CHECKED", "CHECKED\n"),
    ("Incident reference", "lab-267\n"),
    (f"type RELEASE {owner}:", f"RELEASE {owner}\n"),
], 0)
assert "Released ownership generation 7" in output, output
assert sql("SELECT owner_id IS NULL FROM payout_manager_ownership") == "t"
assert sql(f"SELECT count(*) FROM {audit_table} WHERE owner_id='{owner}' AND incident_reference='lab-267'") == "1"
assert sql(f"SELECT NOT has_table_privilege(current_user, '{audit_table}', 'INSERT,UPDATE,DELETE,TRUNCATE')") == "t"
assert sql("SELECT NOT rolsuper FROM pg_roles WHERE rolname=current_user") == "t"
assert sql(f"SELECT pg_get_userbyid(relowner)<>current_user FROM pg_class WHERE oid='{audit_table}'::regclass") == "t"
print("PASS: explicit terminal recovery clears only the inspected owner and exits successfully")
assert sql("SELECT pg_get_userbyid(datdba)=current_user FROM pg_database WHERE datname=current_database()") == "t"
assert sql("SELECT pg_has_role(current_user, nspowner, 'USAGE') FROM pg_namespace WHERE nspname='public'") == "t"
for statement in (
    f"DROP TABLE {audit_table}", f"DROP SCHEMA {audit_schema} CASCADE",
    f"ALTER TABLE {audit_table} DISABLE TRIGGER ALL",
    f"INSERT INTO {audit_table} SELECT * FROM {audit_table}",
    f"UPDATE {audit_table} SET incident_reference='forged'", f"DELETE FROM {audit_table}", f"TRUNCATE {audit_table}",
):
    denied = subprocess.run(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-c", statement], capture_output=True, text=True)
    assert denied.returncode != 0, statement
assert sql(f"SELECT count(*) FROM {audit_table}") == "1"
print("PASS: an application-owned database cannot drop, directly insert into or rewrite protected audit storage")
append = f"SELECT * FROM {audit_schema}.record_payout_recovery('{uuid.uuid4()}','{uuid.uuid4()}',7::bigint,'forged',123,now(),now(),'forged',123,'forged')"
denied = subprocess.run(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-c", append], capture_output=True, text=True)
assert denied.returncode != 0 and "exclusive payout advisory lock" in denied.stderr, denied.stderr
assert sql(f"SELECT count(*) FROM {audit_table}") == "1"
print("PASS: the pool role cannot append an audit event without holding the payout lock")



for legacy in (True, False):
    document = json.loads(config.read_text())
    document["persistence"]["postgres"]["enableLegacyTimestamps"] = legacy
    config.write_text(json.dumps(document))
    result = subprocess.run(["dotnet", test_host, "payout-timestamp-probe", str(config), str(legacy)],
                            capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stdout + result.stderr
print("PASS: fresh CLI processes apply explicit timestamp behavior before recovery connects")

owner = seed()
keys = json.loads(subprocess.check_output(["dotnet", test_host, "payout-lock-keys"], text=True))
locker = subprocess.Popen(["psql", "-X", "-c",
    f"SELECT pg_advisory_lock({keys['LockNamespace']},{keys['LockKey']}); SELECT pg_sleep(30)"],
    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    for _ in range(100):
        if sql(f"SELECT EXISTS(SELECT FROM pg_locks WHERE locktype='advisory' AND classid={keys['LockNamespace']} AND objid={keys['LockKey']} AND objsubid=2 AND granted)") == "t":
            break
        time.sleep(0.05)
    result = subprocess.run(command, input="", text=True, capture_output=True, timeout=30)
    assert result.returncode == 1, result.stdout + result.stderr
    assert "Choice [1]" not in result.stdout
    assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
finally:
    locker.send_signal(signal.SIGINT)
    locker.wait(timeout=5)
print("PASS: Main returns retryable exit 1 for a live lock, not retained-owner exit 78")

# Normal startup must prompt before opening API/Stratum listeners or wallet clients.
example = (Path(__file__).resolve().parents[2] / "config.example.json").read_text(encoding="utf-8-sig")
# The maintained example uses full-line JSON comments accepted by Miningcore.
normal = json.loads("\n".join(line for line in example.splitlines() if not line.lstrip().startswith("//")))
normal["persistence"] = json.loads(config.read_text())["persistence"]
normal["paymentProcessing"]["enabled"] = True
normal["pools"][0]["enabled"] = True
normal["pools"][0]["paymentProcessing"]["enabled"] = True
normal["logging"] = {"level": "info", "enableConsoleLog": True, "enableConsoleColors": False}
normal["api"] = {"enabled": False}
normal["shareRecoveryFile"] = str(Path(scratch) / "normal-shares.txt")
normal["shareRecoveryStateDirectory"] = scratch
config.write_text(json.dumps(normal))
command = ["dotnet", app, "-c", str(config)]
transcript = terminal([("Choice [1]:", "1\n")], 78)
assert "Starting Pool" not in transcript and "Stratum port" not in transcript
assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
print("PASS: normal terminal startup offers recovery before hosted services start")

for legacy in (True, False):
    normal["persistence"]["postgres"]["enableLegacyTimestamps"] = legacy
    config.write_text(json.dumps(normal))
    command = ["dotnet", test_host, "payout-timestamp-probe", str(config), str(legacy), "startup"]
    terminal([("Choice [1]:", "1\n")], 0)
print("PASS: fresh normal-startup processes apply timestamp behavior before the prompt")

command = ["dotnet", app, "-c", str(config)]
# These minimal fixtures deliberately lack normal mining tables, so preflight must
# fail after bypassing the assistant. No wallet client or listener can start.
result = subprocess.run(command, input="3\n", capture_output=True, text=True, timeout=30)
assert result.returncode == 1, result.stdout + result.stderr
assert "Choice [1]" not in result.stdout and "Owner token:" not in result.stdout
assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
command.append("--no-payout-recovery-prompt")
transcript = terminal([], 1)
assert "Choice [1]" not in transcript and "Owner token:" not in transcript
assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
print("PASS: normal startup skips recovery for redirected input and explicit opt-out")
command.pop()
transcript = terminal([
    ("Choice [1]:", "3\n"), ("Type STOPPED", "STOPPED\n"),
    ("Type CHECKED", "CHECKED\n"), ("Incident reference", "lab-startup-267\n"),
    (f"type RELEASE {owner}:", f"RELEASE {owner}\n"),
], 1)
assert "Released ownership generation 7" in transcript, transcript
assert sql("SELECT owner_id IS NULL FROM payout_manager_ownership") == "t"
print("PASS: successful foreground recovery continues to normal preflight")
transcript = terminal([], 1)
assert "Choice [1]" not in transcript and "No payout ownership marker" not in transcript
print("PASS: unowned normal startup remains quiet and continues to preflight")

normal["persistence"]["postgres"].pop("enableLegacyTimestamps")
config.write_text(json.dumps(normal))
try:
    result = subprocess.run(command, input="", capture_output=True, text=True, timeout=30)
    assert result.returncode == 78, result.stdout + result.stderr
    assert "Set persistence.postgres.enableLegacyTimestamps to true and restart" in result.stdout + result.stderr
finally:
    sql("DROP TABLE shares")
print("PASS: auto-detected legacy timestamps fail closed with exit 78 and an explicit-setting remedy")
result = subprocess.run(command, input="", capture_output=True, text=True, timeout=30)
assert result.returncode == 78, result.stdout + result.stderr
assert "Unable to determine the PostgreSQL timestamp type" in result.stdout + result.stderr
print("PASS: indeterminate timestamp schema exits 78 from Main without restarting")
