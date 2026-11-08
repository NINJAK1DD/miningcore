"""Execute the documented dedicated-login grants/check in the disposable live-test cluster."""
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import uuid

test_host, scratch, migration = sys.argv[1:]
assert os.geteuid() != 0, "Permission probes must run as the unprivileged lab account"
repo = Path(__file__).resolve().parents[2]
document = (repo / "docs/database.md").read_text()
database = "recovery_acl_" + uuid.uuid4().hex
recovery_role = "recovery_" + uuid.uuid4().hex
group = "recovery_group_" + uuid.uuid4().hex
password = secrets.token_hex(32)


def sql(query, target=database, env=None, success=True):
    result = subprocess.run(["psql", "-X", "-At", "-v", "ON_ERROR_STOP=1", "-d", target],
                            input=query, capture_output=True, text=True, env=env, timeout=30)
    assert (result.returncode == 0) == success, result.stdout + result.stderr
    return result.stdout.strip()


def block(name):
    start = f"<!-- BEGIN DEDICATED RECOVERY {name} -->"
    end = f"<!-- END DEDICATED RECOVERY {name} -->"
    section = document.split(start)[1].split(end)[0]
    return section.split("```sql\n")[1].split("```")[0].replace(
        "miningcore_recovery", recovery_role).replace("DATABASE miningcore", "DATABASE " + database)


def verify(success=True):
    result = subprocess.run(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-d", database],
                            input=block("CHECK"), capture_output=True, text=True, timeout=30)
    assert (result.returncode == 0) == success, result.stdout + result.stderr
    assert ("Dedicated recovery permission check passed" in result.stdout if success else
            "Dedicated recovery permission check failed" in result.stderr), result.stdout + result.stderr


def migrate():
    subprocess.run(["psql", "-X", "-v", "ON_ERROR_STOP=1", "-d", database, "-f", migration],
                   check=True, stdout=subprocess.DEVNULL, timeout=30)


sql(f"CREATE DATABASE {database} OWNER miningcore", os.environ["PGDATABASE"])
try:
    sql(f"""CREATE ROLE {recovery_role} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
        CREATE ROLE {group};
        ALTER ROLE {recovery_role} IN DATABASE {database} SET search_path TO public;
        REVOKE CONNECT ON DATABASE {database} FROM PUBLIC;
        CREATE TABLE payout_manager_ownership(id smallint PRIMARY KEY CHECK(id=1), generation bigint NOT NULL DEFAULT 7,
            owner_id uuid, owner_host text, owner_process_id integer, acquired timestamptz, released timestamptz);
        CREATE TABLE payment_batches(poolid text NOT NULL, transactionconfirmationdata text NOT NULL, created timestamptz NOT NULL,
            PRIMARY KEY(poolid,transactionconfirmationdata));
        ALTER TABLE payout_manager_ownership OWNER TO miningcore;
        ALTER TABLE payment_batches OWNER TO miningcore;
        INSERT INTO payment_batches VALUES('synthetic','wallet-evidence',now());""")
    migrate()
    verify(False)
    sql(block("GRANTS"))
    verify()
    audit = sql("SELECT 'miningcore_audit_' || 'payout_manager_ownership'::regclass::oid::text")
    function = audit + ".record_payout_recovery(uuid,uuid,bigint,text,integer,timestamptz,timestamptz,text,integer,text)"
    for privilege, resource in [
        ("CONNECT", "DATABASE " + database), ("USAGE", "SCHEMA public"),
        ("SELECT", "TABLE public.payout_manager_ownership"), ("UPDATE", "TABLE public.payout_manager_ownership"),
        ("SELECT", "TABLE public.payment_batches"), ("USAGE", "SCHEMA " + audit),
        ("SELECT", "TABLE " + audit + ".payout_manager_recovery_events"), ("EXECUTE", "FUNCTION " + function),
    ]:
        # public grants schema USAGE by default; remove that alternative for this negative control.
        if resource == "SCHEMA public":
            sql("REVOKE USAGE ON SCHEMA public FROM PUBLIC")
        sql(f"REVOKE {privilege} ON {resource} FROM {recovery_role}")
        verify(False)
        sql(block("GRANTS"))
        verify()
    print("PASS: documented dedicated-recovery grants/check cover every required privilege")

    sql(f"GRANT EXECUTE ON FUNCTION {function} TO {group}; GRANT {group} TO miningcore")
    verify(False)
    sql("ALTER ROLE miningcore NOINHERIT")
    try:
        verify(False)
    finally:
        sql("ALTER ROLE miningcore INHERIT")
    sql(f"REVOKE {group} FROM miningcore; REVOKE EXECUTE ON FUNCTION {function} FROM {group}")
    sql(f"ALTER ROLE miningcore NOINHERIT; GRANT {recovery_role} TO miningcore")
    try:
        verify(False)
    finally:
        sql(f"REVOKE {recovery_role} FROM miningcore; ALTER ROLE miningcore INHERIT")
    sql(f"GRANT EXECUTE ON FUNCTION {function} TO PUBLIC")
    verify(False)
    sql(block("GRANTS"))
    verify()
    print("PASS: acceptance rejects PUBLIC/inherited execute and non-inherited recovery-role membership")

    migrate()
    verify(False)
    sql(block("GRANTS"))
    verify()
    print("PASS: migration-restored application access blocks acceptance until restrictions are reapplied")

    owner = str(uuid.uuid4())
    sql(f"INSERT INTO payout_manager_ownership(id,owner_id,owner_host,owner_process_id,acquired) VALUES(1,'{owner}','synthetic',123,now())")
    app_password = (Path(scratch) / "app.pgpass").read_text().strip().split(":")[-1]
    for role, secret, expected in [("miningcore", app_password, 78), (recovery_role, password, 0)]:
        config = Path(scratch) / "dedicated-recovery.json"
        config.write_text(json.dumps({"persistence": {"postgres": {
            "host": os.environ["PGHOST"], "port": int(os.environ["PGPORT"]), "database": database,
            "user": role, "password": secret, "enableLegacyTimestamps": False}}, "pools": []}))
        config.chmod(0o600)
        result = subprocess.run(["dotnet", test_host, "payout-release-probe", str(config)],
                                capture_output=True, text=True, timeout=30)
        assert result.returncode == expected, result.stdout + result.stderr
        if expected:
            assert "audit storage is unavailable" in result.stderr
            assert sql("SELECT owner_id FROM payout_manager_ownership") == owner
    assert sql("SELECT owner_id IS NULL FROM payout_manager_ownership") == "t"
    assert sql(f"SELECT database_role FROM {audit}.payout_manager_recovery_events") == recovery_role
    # Actual recovery login retains read access but cannot write audit rows directly.
    recovery_env = dict(os.environ, PGUSER=recovery_role, PGPASSWORD=password)
    assert sql(f"SELECT count(*) FROM {audit}.payout_manager_recovery_events", env=recovery_env) == "1"
    sql(f"DELETE FROM {audit}.payout_manager_recovery_events", env=recovery_env, success=False)
    print("PASS: actual application login keeps ownership; restricted recovery login releases and records its identity")
finally:
    sql(f"DROP DATABASE {database} WITH (FORCE)", os.environ["PGDATABASE"])
    sql(f"DROP ROLE IF EXISTS {recovery_role}, {group}", os.environ["PGDATABASE"])
