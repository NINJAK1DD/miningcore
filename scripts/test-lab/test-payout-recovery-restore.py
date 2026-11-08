"""Logical restore drill against the disposable live-test cluster, never a real wallet."""
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

test_host, scratch, migration = sys.argv[1:]
assert os.geteuid() != 0, "Restore probes must run as the unprivileged lab account"
source = os.environ["PGDATABASE"]
target = "recovery_restore_" + uuid.uuid4().hex


def sql(query, database=source):
    return subprocess.check_output(["psql", "-X", "-At", "-v", "ON_ERROR_STOP=1", "-d", database,
                                    "-c", query], text=True).strip()


old_oid = sql("SELECT 'payout_manager_ownership'::regclass::oid")
old_table = f"miningcore_audit_{old_oid}.payout_manager_recovery_events"
history_query = f"SELECT event_id,owner_id,generation,incident_reference FROM {old_table} ORDER BY event_id"
history = sql(history_query)
assert history, "The source fixture must contain committed recovery receipts"
archive = str(Path(scratch) / "recovery.dump")
subprocess.run(["pg_dump", "-Fc", "-f", archive, "-d", source], check=True)
sql(f"CREATE DATABASE {target} OWNER miningcore")
try:
    subprocess.run(["pg_restore", "--exit-on-error", "-d", target, archive], check=True)
    new_oid = sql("SELECT 'payout_manager_ownership'::regclass::oid", target)
    assert new_oid != old_oid, "Restore must exercise a changed ownership OID"
    owner = str(uuid.uuid4())
    sql(f"UPDATE payout_manager_ownership SET owner_id='{owner}',owner_host='synthetic-restore',owner_process_id=123,acquired=now()", target)
    config = Path(scratch) / "restore-recovery.json"
    config.write_text(json.dumps({"persistence": {"postgres": {
        "host": os.environ["PGHOST"], "port": int(os.environ["PGPORT"]), "database": target,
        "user": "miningcore", "password": (Path(scratch) / "app.pgpass").read_text().strip().split(":")[-1],
        "enableLegacyTimestamps": False}}, "pools": []}))
    config.chmod(0o600)
    command = ["dotnet", test_host, "payout-release-probe", str(config)]
    refused = subprocess.run(command, capture_output=True, text=True, timeout=30)
    assert refused.returncode == 78 and "audit storage is unavailable" in refused.stderr, refused.stderr
    assert sql("SELECT owner_id FROM payout_manager_ownership", target) == owner
    assert sql(history_query, target) == history
    print("PASS: logical restore changes OIDs and safely refuses release before audit reinitialization")
    subprocess.run(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-d", target, "-f", migration],
                   check=True, stdout=subprocess.DEVNULL)
    new_table = f"miningcore_audit_{new_oid}.payout_manager_recovery_events"
    assert sql(f"SELECT count(*) FROM {new_table}", target) == "0"
    released = subprocess.run(command, capture_output=True, text=True, timeout=30)
    assert released.returncode == 0, released.stdout + released.stderr
    assert sql("SELECT owner_id IS NULL FROM payout_manager_ownership", target) == "t"
    assert sql(f"SELECT count(*) FROM {new_table} WHERE owner_id='{owner}' AND database_role='miningcore'", target) == "1"
    assert sql(history_query, target) == history
    assert sql(history_query) == history
    print("PASS: post-restore migration enables guarded release and preserves all historical receipts")
finally:
    sql(f"DROP DATABASE {target} WITH (FORCE)")
