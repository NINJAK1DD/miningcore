-- Required before enabling PPS or non-SOLO Litecoin-Dogecoin merged mining on an
-- existing database. Stop all Miningcore writers and take a verified backup first.
\set ON_ERROR_STOP on

BEGIN;

ALTER TABLE shares ADD COLUMN IF NOT EXISTS accountingid UUID NULL;
ALTER TABLE shares ADD COLUMN IF NOT EXISTS accountingrole SMALLINT NULL;
ALTER TABLE shares ADD COLUMN IF NOT EXISTS rewardbasissatoshis BIGINT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS IDX_SHARES_POOL_ACCOUNTING
    ON shares(poolid, accountingid) WHERE accountingid IS NOT NULL;
CREATE INDEX IF NOT EXISTS IDX_SHARES_ACCOUNTING
    ON shares(accountingid) WHERE accountingid IS NOT NULL;

CREATE TABLE IF NOT EXISTS share_accounting_groups
(
    accountingid UUID NOT NULL PRIMARY KEY,
    projectioncount SMALLINT NOT NULL,
    payloadhash CHAR(64) NOT NULL,
    created TIMESTAMPTZ NOT NULL
    ,CONSTRAINT CK_SHARE_ACCOUNTING_PROJECTION_COUNT
        CHECK(projectioncount IN (1, 2))
    ,CONSTRAINT CK_SHARE_ACCOUNTING_PAYLOAD_HASH
        CHECK(payloadhash ~ '^[0-9A-F]{64}$')
);
DO $index_cleanup$
BEGIN
    EXECUTE format('DROP INDEX IF EXISTS %I.%I', current_schema(),
        'idx_share_accounting_groups_prune');
    EXECUTE format('DROP INDEX IF EXISTS %I.%I', current_schema(),
        'idx_share_accounting_groups_created');
END
$index_cleanup$;
CREATE INDEX IDX_SHARE_ACCOUNTING_GROUPS_PRUNE
    ON share_accounting_groups(created, accountingid);

CREATE TABLE IF NOT EXISTS share_accounting_prune_state
(
    singletonid SMALLINT NOT NULL PRIMARY KEY,
    cursorcreated TIMESTAMPTZ NULL,
    cursoraccountingid UUID NULL,
    CONSTRAINT CK_SHARE_ACCOUNTING_PRUNE_SINGLETON CHECK(singletonid = 1),
    CONSTRAINT CK_SHARE_ACCOUNTING_PRUNE_CURSOR CHECK(
        (cursorcreated IS NULL AND cursoraccountingid IS NULL)
        OR (cursorcreated IS NOT NULL AND cursoraccountingid IS NOT NULL))
);
INSERT INTO share_accounting_prune_state(singletonid) VALUES(1)
ON CONFLICT(singletonid) DO NOTHING;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'shares'::regclass
          AND conname = 'ck_shares_accounting_tuple') THEN
        ALTER TABLE shares ADD CONSTRAINT CK_SHARES_ACCOUNTING_TUPLE CHECK(
            (accountingid IS NULL AND accountingrole IS NULL
                AND rewardbasissatoshis IS NULL)
            OR (accountingid IS NOT NULL AND accountingrole IN (1, 2, 3)
                AND rewardbasissatoshis > 0));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'shares'::regclass
          AND conname = 'fk_shares_accounting_group') THEN
        ALTER TABLE shares ADD CONSTRAINT FK_SHARES_ACCOUNTING_GROUP
            FOREIGN KEY(accountingid)
            REFERENCES share_accounting_groups(accountingid);
    END IF;
END
$$;

CREATE TABLE IF NOT EXISTS pps_share_credits
(
    poolid TEXT NOT NULL,
    accountingid UUID NOT NULL,
    address TEXT NOT NULL,
    calculatedamount DECIMAL(38,24) NOT NULL,
    creditedamount DECIMAL(28,12) NOT NULL,
    difficulty DOUBLE PRECISION NOT NULL,
    networkdifficulty DOUBLE PRECISION NOT NULL,
    rewardbasissatoshis BIGINT NOT NULL,
    created TIMESTAMPTZ NOT NULL,
    PRIMARY KEY(poolid, accountingid),
    FOREIGN KEY(accountingid) REFERENCES share_accounting_groups(accountingid),
    CONSTRAINT CK_PPS_CALCULATED_AMOUNT CHECK(calculatedamount > 0),
    CONSTRAINT CK_PPS_CREDITED_AMOUNT CHECK(creditedamount >= 0),
    CONSTRAINT CK_PPS_DIFFICULTY CHECK(difficulty > 0),
    CONSTRAINT CK_PPS_NETWORK_DIFFICULTY CHECK(networkdifficulty > 0),
    CONSTRAINT CK_PPS_REWARD_BASIS CHECK(rewardbasissatoshis > 0)
);
CREATE INDEX IF NOT EXISTS IDX_PPS_SHARE_CREDITS_ACCOUNTING
    ON pps_share_credits(accountingid);
CREATE INDEX IF NOT EXISTS IDX_PPS_SHARE_CREDITS_CREATED
    ON pps_share_credits(created);
CREATE INDEX IF NOT EXISTS IDX_BALANCE_CHANGES_PPS_CREATED
    ON balance_changes(created) WHERE usage = 'PPS share credit';

CREATE TABLE IF NOT EXISTS pps_credit_remainders
(
    poolid TEXT NOT NULL,
    address TEXT NOT NULL,
    amount DECIMAL(38,24) NOT NULL,
    updated TIMESTAMPTZ NOT NULL,
    PRIMARY KEY(poolid, address),
    CONSTRAINT CK_PPS_REMAINDER_RANGE
        CHECK(amount >= 0 AND amount < 0.000000000001)
);

-- Upgrade commands run as administrator; use the existing shares owner as the application role.
-- Align newly created or pre-existing accounting relations with that application role.
DO $$
DECLARE
    application_owner NAME;
    target_schema NAME := current_schema();
    relation_name NAME;
BEGIN
    SELECT pg_get_userbyid(relowner)
    INTO application_owner
    FROM pg_class
    WHERE oid = 'shares'::regclass;

    IF application_owner IS NULL THEN
        RAISE EXCEPTION 'Could not resolve the application owner of shares';
    END IF;

    FOREACH relation_name IN ARRAY ARRAY[
        'share_accounting_groups'::NAME,
        'share_accounting_prune_state'::NAME,
        'pps_share_credits'::NAME,
        'pps_credit_remainders'::NAME
    ]
    LOOP
        EXECUTE format('ALTER TABLE %I.%I OWNER TO %I',
            target_schema, relation_name, application_owner);
    END LOOP;
END $$;

COMMIT;

-- BEGIN GENERATED PPS ARITHMETIC MIGRATION
-- Source: add_pps_arithmetic_version.sql; run scripts/release/sync-pps-arithmetic-migration.py
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
-- Check the session login before touching a fresh, partial or existing installation.
DO $$ DECLARE app_role NAME; admin_role NAME := session_user; BEGIN
    SELECT pg_get_userbyid(relowner) INTO app_role FROM pg_class WHERE oid='pps_share_credits'::regclass;
    IF app_role = admin_role AND NOT EXISTS (
        SELECT 1 FROM pg_roles WHERE rolname=admin_role AND rolsuper) THEN
        RAISE EXCEPTION 'Run the PPS arithmetic migration as a separate database administrator';
    END IF;
END $$;
ALTER TABLE pps_share_credits ADD COLUMN IF NOT EXISTS arithmeticversion SMALLINT NOT NULL DEFAULT 0;
-- Repair default drift without rewriting any existing credit or amount.
ALTER TABLE pps_share_credits ALTER COLUMN arithmeticversion SET DEFAULT 0;
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
