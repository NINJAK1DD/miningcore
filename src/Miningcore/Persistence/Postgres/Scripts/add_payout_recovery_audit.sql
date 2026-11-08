-- Stop payout managers. Run as a separate administrator in the ownership-table schema.
-- Existing balances, payment batches and ownership markers are not modified.
BEGIN;
SET LOCAL ROLE NONE;
CREATE INDEX IF NOT EXISTS idx_payment_batches_recovery_created
    ON payment_batches(created DESC, poolid, transactionconfirmationdata);

CREATE TABLE IF NOT EXISTS payout_manager_recovery_events
(
    event_id UUID PRIMARY KEY,
    database_name TEXT NOT NULL,
    database_role TEXT NOT NULL,
    ownership_schema TEXT NOT NULL,
    ownership_relation BIGINT NOT NULL,
    owner_id UUID NOT NULL,
    generation BIGINT NOT NULL,
    owner_host TEXT,
    owner_process_id INT,
    acquired TIMESTAMPTZ,
    released TIMESTAMPTZ NOT NULL,
    releasing_host TEXT NOT NULL,
    releasing_process_id INT NOT NULL,
    incident_reference TEXT NOT NULL CHECK(length(btrim(incident_reference)) BETWEEN 1 AND 200),
    UNIQUE(ownership_relation, owner_id, generation)
);

CREATE OR REPLACE FUNCTION guard_payout_recovery_events() RETURNS TRIGGER
LANGUAGE plpgsql SET search_path TO pg_catalog AS $$
BEGIN
    RAISE EXCEPTION 'Payout recovery audit events are append-only' USING ERRCODE='55000';
END $$;
DROP TRIGGER IF EXISTS trg_payout_recovery_events_append_only ON payout_manager_recovery_events;
CREATE TRIGGER trg_payout_recovery_events_append_only
    BEFORE UPDATE OR DELETE OR TRUNCATE ON payout_manager_recovery_events
    FOR EACH STATEMENT EXECUTE FUNCTION guard_payout_recovery_events();

DO $$
DECLARE app_role NAME; admin_role NAME := session_user; grantee NAME;
BEGIN
    SELECT pg_get_userbyid(relowner) INTO app_role FROM pg_class
        WHERE oid='payout_manager_ownership'::regclass;
    IF app_role = admin_role AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname=admin_role AND rolsuper) THEN
        RAISE EXCEPTION 'Run the payout recovery audit migration as a separate database administrator';
    END IF;
    IF app_role <> admin_role AND pg_has_role(app_role, admin_role, 'MEMBER') THEN
        RAISE EXCEPTION 'The pool role must not inherit the recovery audit administrator role';
    END IF;
    EXECUTE format('ALTER TABLE payout_manager_recovery_events OWNER TO %I', admin_role);
    EXECUTE format('ALTER FUNCTION guard_payout_recovery_events() OWNER TO %I', admin_role);
    FOR grantee IN
        SELECT DISTINCT pg_get_userbyid(a.grantee) FROM pg_class c, LATERAL aclexplode(c.relacl) a
        WHERE c.oid='payout_manager_recovery_events'::regclass AND a.grantee<>0
            AND a.grantee<>(SELECT oid FROM pg_roles WHERE rolname=admin_role)
    LOOP
        EXECUTE format('REVOKE ALL ON payout_manager_recovery_events FROM %I CASCADE', grantee);
    END LOOP;
    REVOKE ALL ON payout_manager_recovery_events FROM PUBLIC;
    REVOKE ALL ON FUNCTION guard_payout_recovery_events() FROM PUBLIC;
    EXECUTE format('GRANT SELECT, INSERT ON payout_manager_recovery_events TO %I', app_role);
END $$;
COMMIT;
