\set ON_ERROR_STOP on
-- Stop all share producers/relays and drain recovery journals before upgrading.
-- Additive metadata only: existing amounts, balances, remainders and hashes stay unchanged.
BEGIN;
-- The session login is the migration administrator, even when createdb.sql
-- selected the application role. SET LOCAL restores the caller's role at COMMIT.
-- Preserve the target schema before changing the role ($user may otherwise resolve differently).
DO $$ BEGIN
    PERFORM set_config('search_path', format('%I, pg_catalog', current_schema()), true);
END $$;
SET LOCAL ROLE NONE;
ALTER TABLE pps_share_credits ADD COLUMN IF NOT EXISTS arithmeticversion SMALLINT NOT NULL DEFAULT 0;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='pps_share_credits'::regclass AND conname='ck_pps_arithmetic_version') THEN
        ALTER TABLE pps_share_credits ADD CONSTRAINT ck_pps_arithmetic_version CHECK(arithmeticversion IN (0,1));
    END IF;
END $$;
CREATE TABLE IF NOT EXISTS pps_arithmetic_transitions (
    poolid TEXT PRIMARY KEY,
    effectivefrom TIMESTAMPTZ NOT NULL CHECK(isfinite(effectivefrom)),
    version SMALLINT NOT NULL CHECK(version=1)
);
DROP TRIGGER IF EXISTS trg_pps_arithmetic_transition_truncate ON pps_arithmetic_transitions;
CREATE OR REPLACE FUNCTION guard_pps_arithmetic_transition() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN
    RAISE EXCEPTION 'PPS arithmetic transitions are immutable; reconcile explicitly';
END $$;
DROP TRIGGER IF EXISTS trg_pps_arithmetic_transition_immutable ON pps_arithmetic_transitions;
CREATE TRIGGER trg_pps_arithmetic_transition_immutable BEFORE UPDATE OR DELETE ON pps_arithmetic_transitions
FOR EACH ROW EXECUTE FUNCTION guard_pps_arithmetic_transition();
CREATE TRIGGER trg_pps_arithmetic_transition_truncate BEFORE TRUNCATE ON pps_arithmetic_transitions
FOR EACH STATEMENT EXECUTE FUNCTION guard_pps_arithmetic_transition();
CREATE OR REPLACE FUNCTION guard_pps_arithmetic_credit() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE cutoff TIMESTAMPTZ; expected SMALLINT;
BEGIN
    SELECT effectivefrom INTO cutoff FROM pps_arithmetic_transitions WHERE poolid=NEW.poolid;
    expected := CASE WHEN cutoff IS NOT NULL AND NEW.created>=cutoff THEN 1 ELSE 0 END;
    IF NEW.arithmeticversion<>expected THEN
        RAISE EXCEPTION 'PPS arithmetic version conflicts with durable cutover';
    END IF;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_pps_arithmetic_credit ON pps_share_credits;
CREATE TRIGGER trg_pps_arithmetic_credit BEFORE INSERT ON pps_share_credits
FOR EACH ROW EXECUTE FUNCTION guard_pps_arithmetic_credit();
CREATE OR REPLACE FUNCTION activate_pps_binary64(scope TEXT, cutoff TIMESTAMPTZ) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE previous TIMESTAMPTZ;
BEGIN
    IF scope IS NULL OR scope='' OR cutoff IS NULL OR NOT isfinite(cutoff) THEN
        RAISE EXCEPTION 'PPS cutover needs a scope and finite timestamp';
    END IF;
    -- Serializes activation against all concurrent credit inserts and activations.
    LOCK TABLE pps_share_credits IN SHARE ROW EXCLUSIVE MODE;
    SELECT effectivefrom INTO previous FROM pps_arithmetic_transitions WHERE poolid=scope;
    IF previous IS NOT NULL THEN
        IF previous<>cutoff THEN RAISE EXCEPTION 'PPS cutover already fixed at another boundary'; END IF;
        RETURN;
    END IF;
    IF EXISTS(SELECT 1 FROM pps_share_credits WHERE poolid=scope AND created>=cutoff) THEN
        RAISE EXCEPTION 'PPS cutover would relabel existing liabilities';
    END IF;
    INSERT INTO pps_arithmetic_transitions VALUES(scope,cutoff,1);
END $$;
REVOKE ALL ON FUNCTION activate_pps_binary64(TEXT,TIMESTAMPTZ) FROM PUBLIC;
-- The credit table owner is the application role; the database owner may differ.
-- An application login must never own or activate the administrative boundary.
DO $$ DECLARE app_role NAME; admin_role NAME := session_user; routine TEXT; BEGIN
    SELECT pg_get_userbyid(relowner) INTO app_role FROM pg_class WHERE oid='pps_share_credits'::regclass;
    IF app_role = admin_role AND NOT EXISTS (
        SELECT 1 FROM pg_roles WHERE rolname=admin_role AND rolsuper) THEN
        RAISE EXCEPTION 'Run the PPS arithmetic migration as a separate database administrator';
    END IF;
    EXECUTE format('ALTER TABLE pps_arithmetic_transitions OWNER TO %I', admin_role);
    REVOKE ALL ON pps_arithmetic_transitions FROM PUBLIC;
    EXECUTE format('REVOKE ALL ON pps_arithmetic_transitions FROM %I', app_role);
    EXECUTE format('GRANT SELECT ON pps_arithmetic_transitions TO %I', app_role);
    FOREACH routine IN ARRAY ARRAY['guard_pps_arithmetic_transition()', 'guard_pps_arithmetic_credit()',
        'activate_pps_binary64(text,timestamptz)'] LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO %I', routine, admin_role);
        EXECUTE format('ALTER FUNCTION %s SET search_path TO pg_catalog, %I, pg_temp', routine, current_schema());
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', routine);
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I', routine, app_role);
    END LOOP;
END $$;
COMMIT;
