-- Apply as a separate database administrator; grant application DML only on operational tables.
-- Re-run this idempotent migration when upgrading Pearl payout hold support.
CREATE TABLE IF NOT EXISTS pearl_candidates (
    poolid TEXT NOT NULL,
    identity TEXT NOT NULL CHECK (identity ~ '^[0-9a-f]{64}$'),
    header BYTEA NOT NULL CHECK (octet_length(header) = 76),
    transactions BYTEA NOT NULL CHECK (octet_length(transactions) BETWEEN 1 AND 4001000),
    proof BYTEA NOT NULL CHECK (octet_length(proof) BETWEEN 1 AND 8388608),
    target BYTEA NOT NULL CHECK (octet_length(target) = 32),
    share TEXT NOT NULL CHECK (octet_length(share) <= 16384),
    block BYTEA CHECK (octet_length(block) BETWEEN 1 AND 4066200),
    blockhash TEXT CHECK (blockhash ~ '^[0-9a-f]{64}$'),
    created TIMESTAMPTZ NOT NULL,
    lastattempt TIMESTAMPTZ,
    PRIMARY KEY (poolid, identity),
    CHECK ((block IS NULL) = (blockhash IS NULL))
);
CREATE INDEX IF NOT EXISTS idx_pearl_candidates_created ON pearl_candidates(created);
ALTER TABLE pearl_candidates ADD COLUMN IF NOT EXISTS lastattempt TIMESTAMPTZ;
ALTER TABLE pearl_candidates ADD COLUMN IF NOT EXISTS retired TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS pearl_payout_intents (
    poolid TEXT NOT NULL PRIMARY KEY,
    marker TEXT NOT NULL UNIQUE CHECK (length(marker) BETWEEN 60 AND 90),
    recipients TEXT NOT NULL CHECK (octet_length(recipients) BETWEEN 2 AND 131072),
    transactionid TEXT CHECK (transactionid ~ '^[0-9a-f]{64}$'),
    created TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS pearl_payout_holds (
    poolid TEXT NOT NULL,
    blockid BIGINT NOT NULL REFERENCES blocks(id) ON DELETE RESTRICT,
    blockhash TEXT NOT NULL CHECK (blockhash ~ '^[0-9a-f]{64}$'),
    PRIMARY KEY (poolid, blockid)
);

CREATE TABLE IF NOT EXISTS pearl_payout_intent_releases (
    poolid TEXT NOT NULL,
    marker TEXT NOT NULL,
    recipients TEXT NOT NULL,
    created TIMESTAMPTZ NOT NULL,
    released TIMESTAMPTZ NOT NULL DEFAULT now(),
    releasedby TEXT NOT NULL,
    auditsha256 TEXT NOT NULL CHECK (auditsha256 ~ '^[0-9a-f]{64}$'),
    reason TEXT NOT NULL CHECK (length(reason) BETWEEN 16 AND 2048),
    PRIMARY KEY (poolid, marker)
);

-- BEGIN PEARL AUDIT PRIVILEGES
-- Run with a separate administrator login. The pool-table owner is the app role.
BEGIN;
DO $$ BEGIN
    PERFORM set_config('search_path', format('%I, pg_catalog', current_schema()), true);
END $$;
SET LOCAL ROLE NONE;
DO $$ DECLARE app_role NAME; admin_role NAME := session_user; grantee NAME; BEGIN
    SELECT pg_get_userbyid(relowner) INTO app_role FROM pg_class WHERE oid='blocks'::regclass;
    IF app_role = admin_role AND NOT EXISTS (
        SELECT 1 FROM pg_roles WHERE rolname=admin_role AND rolsuper) THEN
        RAISE EXCEPTION 'Run the Pearl audit migration as a separate database administrator';
    END IF;
    IF app_role <> admin_role AND pg_has_role(app_role, admin_role, 'MEMBER') THEN
        RAISE EXCEPTION 'The pool role must not inherit the Pearl audit administrator role';
    END IF;
    EXECUTE format('ALTER TABLE pearl_payout_intent_releases OWNER TO %I', admin_role);
    -- Repair historical/default grants, including grants inherited through other roles.
    FOR grantee IN
        SELECT DISTINCT pg_get_userbyid(a.grantee)
        FROM pg_class c, LATERAL aclexplode(c.relacl) a
        WHERE c.oid='pearl_payout_intent_releases'::regclass AND a.grantee<>0
            AND a.grantee<>(SELECT oid FROM pg_roles WHERE rolname=admin_role)
    LOOP
        EXECUTE format('REVOKE ALL ON pearl_payout_intent_releases FROM %I CASCADE', grantee);
    END LOOP;
    REVOKE ALL ON pearl_payout_intent_releases FROM PUBLIC CASCADE;
    EXECUTE format('REVOKE ALL ON pearl_payout_intent_releases FROM %I CASCADE', app_role);
    EXECUTE format('GRANT SELECT ON pearl_payout_intent_releases TO %I', app_role);
END $$;
-- Column-level privileges are independent of table grants.
DO $$ DECLARE entry RECORD; BEGIN
    FOR entry IN
        SELECT DISTINCT at.attname, acl.grantee
        FROM pg_attribute at, LATERAL aclexplode(at.attacl) acl
        WHERE at.attrelid='pearl_payout_intent_releases'::regclass AND at.attnum>0
            AND acl.grantee<>(SELECT oid FROM pg_roles WHERE rolname=session_user)
    LOOP
        EXECUTE format('REVOKE ALL (%I) ON pearl_payout_intent_releases FROM %s CASCADE',
            entry.attname, CASE WHEN entry.grantee=0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(entry.grantee)) END);
    END LOOP;
END $$;
COMMIT;
-- END PEARL AUDIT PRIVILEGES
