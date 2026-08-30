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

-- Upgrade commands are run by an administrator, but Miningcore connects as the database owner.
-- Align newly created or pre-existing accounting relations with that application role.
DO $$
DECLARE
    database_owner NAME;
    target_schema NAME := current_schema();
    relation_name NAME;
BEGIN
    SELECT pg_get_userbyid(datdba)
    INTO database_owner
    FROM pg_database
    WHERE datname = current_database();

    IF database_owner IS NULL THEN
        RAISE EXCEPTION 'Could not resolve the owner of database %', current_database();
    END IF;

    FOREACH relation_name IN ARRAY ARRAY[
        'share_accounting_groups'::NAME,
        'share_accounting_prune_state'::NAME,
        'pps_share_credits'::NAME,
        'pps_credit_remainders'::NAME
    ]
    LOOP
        EXECUTE format('ALTER TABLE %I.%I OWNER TO %I',
            target_schema, relation_name, database_owner);
    END LOOP;
END $$;

COMMIT;
