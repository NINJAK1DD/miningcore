-- Stop all share producers/relays and drain recovery journals before upgrading.
-- Additive metadata only: existing amounts, balances, remainders and hashes stay unchanged.
BEGIN;
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
CREATE OR REPLACE FUNCTION guard_pps_arithmetic_transition() RETURNS trigger
LANGUAGE plpgsql AS $$ BEGIN
    RAISE EXCEPTION 'PPS arithmetic transitions are immutable; reconcile explicitly';
END $$;
DROP TRIGGER IF EXISTS trg_pps_arithmetic_transition_immutable ON pps_arithmetic_transitions;
CREATE TRIGGER trg_pps_arithmetic_transition_immutable BEFORE UPDATE OR DELETE ON pps_arithmetic_transitions
FOR EACH ROW EXECUTE FUNCTION guard_pps_arithmetic_transition();
CREATE OR REPLACE FUNCTION guard_pps_arithmetic_credit() RETURNS trigger
LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
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
LANGUAGE plpgsql SET search_path FROM CURRENT AS $$
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
-- Give the database application owner access to transition reads used by the trigger.
DO $$ DECLARE owner_name NAME; BEGIN
    SELECT pg_get_userbyid(datdba) INTO owner_name FROM pg_database WHERE datname=current_database();
    EXECUTE format('GRANT SELECT ON pps_arithmetic_transitions TO %I',owner_name);
END $$;
COMMIT;
