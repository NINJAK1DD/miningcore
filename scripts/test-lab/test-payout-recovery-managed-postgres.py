"""Verify the documented managed PostgreSQL migration without a superuser session."""
import os
from pathlib import Path
import secrets
import subprocess
import sys
import uuid

assert os.geteuid() != 0
repo = Path(__file__).resolve().parents[2]
database = "managed_recovery_" + uuid.uuid4().hex
administrator = "migration_admin_" + uuid.uuid4().hex
password = secrets.token_hex(32)
admin_env = dict(os.environ, PGUSER=administrator, PGPASSWORD=password)
app_password = (Path(sys.argv[1]) / "app.pgpass").read_text().strip().split(":")[-1]
app_env = dict(os.environ, PGUSER="miningcore", PGPASSWORD=app_password)


def sql(query, env=None, success=True, target=database):
    result = subprocess.run(["psql", "-X", "-At", "-v", "ON_ERROR_STOP=1", "-d", target],
                            input=query, capture_output=True, text=True, env=env, timeout=30)
    assert (result.returncode == 0) == success, result.stdout + result.stderr
    return result.stdout.strip() if success else result.stderr


sql(f"CREATE DATABASE {database} OWNER miningcore", target=os.environ["PGDATABASE"])
try:
    sql(f"CREATE ROLE {administrator} LOGIN PASSWORD '{password}' INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS")
    sql("""CREATE TABLE payout_manager_ownership(id smallint PRIMARY KEY CHECK(id=1), generation bigint NOT NULL DEFAULT 0,
        owner_id uuid, owner_host text, owner_process_id integer, acquired timestamptz, released timestamptz);
        CREATE TABLE payment_batches(poolid text NOT NULL, transactionconfirmationdata text NOT NULL,
        created timestamptz NOT NULL, PRIMARY KEY(poolid,transactionconfirmationdata));
        ALTER TABLE payout_manager_ownership OWNER TO miningcore;
        ALTER TABLE payment_batches OWNER TO miningcore;""")
    migration = (repo / "src/Miningcore/Persistence/Postgres/Scripts/add_payout_recovery_audit.sql").read_text()
    assert "must be owner of table payment_batches" in sql(migration, admin_env, False)
    assert sql("SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'miningcore_audit_%'") == "0"
    document = (repo / "docs/database.md").read_text()
    section = document.split("<!-- BEGIN MANAGED RECOVERY GRANTS -->")[1].split("<!-- END MANAGED RECOVERY GRANTS -->")[0]
    grants = section.split("```sql\n")[1].split("```")[0]
    grants = grants.replace("dbadmin", administrator).replace("DATABASE miningcore", "DATABASE " + database)
    if int(sql("SHOW server_version_num")) < 160000:
        grants = grants.replace(" WITH INHERIT TRUE", "")
    sql(grants)
    assert sql("SELECT NOT rolsuper FROM pg_roles WHERE rolname=current_user", admin_env) == "t"
    sql(migration, admin_env)
    audit = sql("SELECT 'miningcore_audit_' || 'payout_manager_ownership'::regclass::oid::text")
    assert sql(f"SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname='{audit}'") == administrator
    assert sql(f"SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid='{audit}.payout_manager_recovery_events'::regclass") == administrator
    assert sql(f"SELECT bool_and(pg_get_userbyid(proowner)='{administrator}') FROM pg_proc WHERE pronamespace='{audit}'::regnamespace") == "t"
    sql(f"REVOKE miningcore FROM {administrator}; REVOKE CREATE ON DATABASE {database} FROM {administrator}")
    assert sql(f"SELECT pg_has_role('miningcore','{administrator}','MEMBER')") == "f"
    assert "must be owner" in sql(f"DROP SCHEMA {audit} CASCADE", app_env, False)
    assert sql(f"SELECT count(*) FROM {audit}.payout_manager_recovery_events", app_env) == "0"
    print("PASS: documented non-superuser migration grants preserve administrator-owned audit isolation")
finally:
    sql(f"DROP DATABASE {database} WITH (FORCE)", target=os.environ["PGDATABASE"])
    sql(f"DROP ROLE IF EXISTS {administrator}", target=os.environ["PGDATABASE"])
