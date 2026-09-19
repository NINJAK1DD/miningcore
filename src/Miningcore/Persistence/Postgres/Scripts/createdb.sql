SET ROLE miningcore;

CREATE TABLE shares
(
	poolid TEXT NOT NULL,
	blockheight BIGINT NOT NULL,
	difficulty DOUBLE PRECISION NOT NULL,
	networkdifficulty DOUBLE PRECISION NOT NULL,
	sharedifficulty DOUBLE PRECISION NULL,
	actualdifficulty DOUBLE PRECISION NULL,
	miner TEXT NOT NULL,
	worker TEXT NULL,
	useragent TEXT NULL,
	ipaddress TEXT NOT NULL,
	source TEXT NULL,
	sessionid TEXT NULL,
	accountingid UUID NULL,
	accountingrole SMALLINT NULL,
	rewardbasissatoshis BIGINT NULL,
	created TIMESTAMPTZ NOT NULL,
	CONSTRAINT CK_SHARES_ACCOUNTING_TUPLE CHECK(
		(accountingid IS NULL AND accountingrole IS NULL AND rewardbasissatoshis IS NULL)
		OR (accountingid IS NOT NULL AND accountingrole IN (1, 2, 3)
			AND rewardbasissatoshis > 0))
);

CREATE INDEX IDX_SHARES_POOL_MINER on shares(poolid, miner);
CREATE INDEX IDX_SHARES_POOL_CREATED ON shares(poolid, created);
CREATE INDEX IDX_SHARES_POOL_MINER_DIFFICULTY on shares(poolid, miner, difficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_SHAREDIFFICULTY on shares(poolid, miner, sharedifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_WORKER_SHAREDIFFICULTY on shares(poolid, miner, worker, sharedifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_ACTUALDIFFICULTY on shares(poolid, miner, actualdifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_SESSION_ACTUALDIFFICULTY on shares(poolid,miner,sessionid,actualdifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_WORKER_SESSION_ACTUALDIFFICULTY on shares(poolid,miner,worker,sessionid,actualdifficulty);
CREATE UNIQUE INDEX IDX_SHARES_POOL_ACCOUNTING ON shares(poolid, accountingid)
    WHERE accountingid IS NOT NULL;
CREATE INDEX IDX_SHARES_ACCOUNTING ON shares(accountingid)
    WHERE accountingid IS NOT NULL;

CREATE TABLE share_accounting_groups
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
CREATE INDEX IDX_SHARE_ACCOUNTING_GROUPS_PRUNE
    ON share_accounting_groups(created, accountingid);

CREATE TABLE share_accounting_prune_state
(
	singletonid SMALLINT NOT NULL PRIMARY KEY,
	cursorcreated TIMESTAMPTZ NULL,
	cursoraccountingid UUID NULL,
	CONSTRAINT CK_SHARE_ACCOUNTING_PRUNE_SINGLETON CHECK(singletonid = 1),
	CONSTRAINT CK_SHARE_ACCOUNTING_PRUNE_CURSOR CHECK(
		(cursorcreated IS NULL AND cursoraccountingid IS NULL)
		OR (cursorcreated IS NOT NULL AND cursoraccountingid IS NOT NULL))
);
INSERT INTO share_accounting_prune_state(singletonid) VALUES(1);

ALTER TABLE shares ADD CONSTRAINT FK_SHARES_ACCOUNTING_GROUP
	FOREIGN KEY(accountingid) REFERENCES share_accounting_groups(accountingid);

CREATE TABLE pps_share_credits
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
CREATE INDEX IDX_PPS_SHARE_CREDITS_ACCOUNTING
    ON pps_share_credits(accountingid);
CREATE INDEX IDX_PPS_SHARE_CREDITS_CREATED
    ON pps_share_credits(created);

CREATE TABLE pps_credit_remainders
(
	poolid TEXT NOT NULL,
	address TEXT NOT NULL,
	amount DECIMAL(38,24) NOT NULL,
	updated TIMESTAMPTZ NOT NULL,
	PRIMARY KEY(poolid, address),
	CONSTRAINT CK_PPS_REMAINDER_RANGE
		CHECK(amount >= 0 AND amount < 0.000000000001)
);

CREATE TABLE share_recovery_imports
(
	filehash TEXT NOT NULL PRIMARY KEY,
	filename TEXT NOT NULL,
	recordcount INT NOT NULL,
	created TIMESTAMPTZ NOT NULL
);

CREATE TABLE blocks
(
	id BIGSERIAL NOT NULL PRIMARY KEY,
	poolid TEXT NOT NULL,
	blockheight BIGINT NOT NULL,
	networkdifficulty DOUBLE PRECISION NOT NULL,
	status TEXT NOT NULL,
    type TEXT NULL,
    confirmationprogress FLOAT NOT NULL DEFAULT 0,
	effort FLOAT NULL,
        minereffort FLOAT NULL,
	transactionconfirmationdata TEXT NOT NULL,
	miner TEXT NULL,
	reward decimal(28,12) NULL,
    source TEXT NULL,
	hash TEXT NULL,
	created TIMESTAMPTZ NOT NULL,
    settlementmode TEXT NULL,
    grossrewardsatoshis BIGINT NULL,
    directminerrewardsatoshis BIGINT NULL,
    directminerscriptpubkey TEXT NULL,
    directrecipientoutputs JSONB NULL,
    directsettlementlastchecked TIMESTAMPTZ NULL,
    directsubmissionstate TEXT NULL,
    directsubmissionblock TEXT NULL,
    directsubmissionattempts INT NULL,
    directsubmissiondefinitivemisses INT NULL,
    directsubmissionlastattempt TIMESTAMPTZ NULL,
    CONSTRAINT CHK_BLOCKS_BITCOIN_DIRECT_SETTLEMENT CHECK (
        (num_nonnulls(settlementmode, grossrewardsatoshis,
            directminerrewardsatoshis, directminerscriptpubkey,
            directrecipientoutputs, directsubmissionstate,
            directsubmissionblock, directsubmissionattempts,
            directsubmissiondefinitivemisses,
            directsubmissionlastattempt) = 0 AND
            directsettlementlastchecked IS NULL AND
            type IS DISTINCT FROM 'bitcoin-coinbase-direct')
        OR
        (num_nonnulls(settlementmode, grossrewardsatoshis,
            directminerrewardsatoshis, directminerscriptpubkey,
            directrecipientoutputs) = 5 AND
            settlementmode = 'coinbase-direct' AND
            type = 'bitcoin-coinbase-direct' AND
            grossrewardsatoshis > 0 AND
            directminerrewardsatoshis > 0 AND
            directminerrewardsatoshis <= grossrewardsatoshis AND
            directminerscriptpubkey ~ '^[0-9a-f]+$' AND
            length(directminerscriptpubkey) % 2 = 0 AND
            jsonb_typeof(directrecipientoutputs) = 'array' AND
            (
                (directsubmissionstate = 'legacy-observed' AND
                    directsubmissionblock IS NULL AND
                    directsubmissionattempts = 0 AND
                    directsubmissiondefinitivemisses = 0 AND
                    directsubmissionlastattempt IS NULL)
                OR
                (directsubmissionstate IN ('prepared',
                        'submitted-uncertain', 'observed-active', 'rejected',
                        'quarantined') AND
                    directsubmissionblock ~ '^[0-9a-f]+$' AND
                    length(directsubmissionblock) BETWEEN 162 AND 8000000 AND
                    length(directsubmissionblock) % 2 = 0 AND
                    directsubmissionattempts >= 0 AND
                    directsubmissiondefinitivemisses >= 0 AND
                    directsubmissiondefinitivemisses <=
                        directsubmissionattempts AND
                    ((directsubmissionstate = 'prepared' AND
                        directsubmissionattempts = 0 AND
                        directsubmissiondefinitivemisses = 0 AND
                        directsubmissionlastattempt IS NULL AND
                        status = 'pending') OR
                     (directsubmissionstate = 'quarantined' AND
                        status = 'quarantined' AND
                        ((directsubmissionattempts = 0 AND
                          directsubmissionlastattempt IS NULL) OR
                         (directsubmissionattempts > 0 AND
                          directsubmissionlastattempt IS NOT NULL))) OR
                     (directsubmissionstate <> 'prepared' AND
                        directsubmissionstate <> 'quarantined' AND
                        directsubmissionattempts > 0 AND
                        directsubmissionlastattempt IS NOT NULL)) AND
                    (directsubmissionstate <> 'submitted-uncertain' OR
                        status = 'pending') AND
                    (directsubmissionstate <> 'rejected' OR
                        (status = 'orphaned' AND
                         directsubmissiondefinitivemisses >= 3))))
        )
    )
);

CREATE INDEX IDX_BLOCKS_POOL_BLOCK_STATUS on blocks(poolid, blockheight, status);
CREATE INDEX IDX_BLOCKS_POOL_BLOCK_TYPE on blocks(poolid, blockheight, type);
CREATE UNIQUE INDEX IDX_BLOCKS_AUXPOW_POOL_HASH on blocks(poolid, hash) WHERE type = 'auxpow';
CREATE UNIQUE INDEX IDX_BLOCKS_AUXPOW_CLAIM on blocks(poolid, hash, (regexp_replace(transactionconfirmationdata, ':[0-9]+$', ''))) WHERE type = 'auxpow-claim';
CREATE UNIQUE INDEX IDX_BLOCKS_MERGED_PARENT_POOL_HASH on blocks(poolid, hash) WHERE type IN ('merged-parent', 'merged-parent-uncertain');
CREATE UNIQUE INDEX IDX_BLOCKS_BITCOIN_DIRECT_POOL_HASH on blocks(poolid, hash) WHERE type = 'bitcoin-direct';
CREATE UNIQUE INDEX IDX_BLOCKS_BITCOIN_COINBASE_DIRECT_POOL_HASH on blocks(poolid, hash) WHERE type = 'bitcoin-coinbase-direct';
CREATE INDEX IDX_BLOCKS_BITCOIN_DIRECT_RECONCILE ON blocks(
    poolid, directsettlementlastchecked ASC NULLS FIRST, created, id,
    blockheight)
    WHERE status IN ('confirmed', 'orphaned') AND
        type = 'bitcoin-coinbase-direct' AND
        settlementmode = 'coinbase-direct';
CREATE INDEX IDX_BLOCKS_BITCOIN_DIRECT_SUBMISSION ON blocks(poolid, id)
    WHERE status = 'pending' AND type = 'bitcoin-coinbase-direct' AND
        settlementmode = 'coinbase-direct' AND
        directsubmissionstate IN ('prepared', 'submitted-uncertain');

CREATE OR REPLACE FUNCTION guard_bitcoin_direct_block_update()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF OLD.settlementmode = 'coinbase-direct' AND
       current_setting('miningcore.direct_settlement_update', true)
           IS DISTINCT FROM 'on' THEN
        RAISE EXCEPTION USING
            ERRCODE = '55000',
            MESSAGE = 'direct-settlement block updates require a compatible Miningcore binary';
    END IF;

    RETURN NEW;
END;
$$;

CREATE TRIGGER TRG_GUARD_BITCOIN_DIRECT_BLOCK_UPDATE
    BEFORE UPDATE ON blocks
    FOR EACH ROW
    EXECUTE FUNCTION guard_bitcoin_direct_block_update();

CREATE OR REPLACE FUNCTION clear_bitcoin_direct_block_update_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    PERFORM set_config('miningcore.direct_settlement_update', 'off', true);
    RETURN NULL;
END;
$$;

CREATE TRIGGER TRG_CLEAR_BITCOIN_DIRECT_BLOCK_UPDATE_GUARD
    AFTER UPDATE ON blocks
    FOR EACH STATEMENT
    EXECUTE FUNCTION clear_bitcoin_direct_block_update_guard();

CREATE TABLE balances
(
	poolid TEXT NOT NULL,
	address TEXT NOT NULL,
	amount decimal(28,12) NOT NULL DEFAULT 0,
	created TIMESTAMPTZ NOT NULL,
	updated TIMESTAMPTZ NOT NULL,

	primary key(poolid, address)
);

CREATE TABLE balance_changes
(
	id BIGSERIAL NOT NULL PRIMARY KEY,
	poolid TEXT NOT NULL,
	address TEXT NOT NULL,
	amount decimal(28,12) NOT NULL DEFAULT 0,
	usage TEXT NULL,
    tags text[] NULL,
	created TIMESTAMPTZ NOT NULL
);

CREATE INDEX IDX_BALANCE_CHANGES_POOL_ADDRESS_CREATED on balance_changes(poolid, address, created desc);
CREATE INDEX IDX_BALANCE_CHANGES_POOL_TAGS on balance_changes USING gin (tags);
CREATE INDEX IDX_BALANCE_CHANGES_PPS_CREATED ON balance_changes(created)
    WHERE usage = 'PPS share credit';

CREATE TABLE miner_settings
(
	poolid TEXT NOT NULL,
	address TEXT NOT NULL,
	paymentthreshold decimal(28,12) NOT NULL,
	created TIMESTAMPTZ NOT NULL,
	updated TIMESTAMPTZ NOT NULL,

	primary key(poolid, address)
);

CREATE TABLE payments
(
	id BIGSERIAL NOT NULL PRIMARY KEY,
	poolid TEXT NOT NULL,
	coin TEXT NOT NULL,
	address TEXT NOT NULL,
	amount decimal(28,12) NOT NULL,
	transactionconfirmationdata TEXT NOT NULL,
	created TIMESTAMPTZ NOT NULL
);

CREATE INDEX IDX_PAYMENTS_POOL_COIN_WALLET on payments(poolid, coin, address);

CREATE TABLE payment_batches
(
	poolid TEXT NOT NULL,
	transactionconfirmationdata TEXT NOT NULL,
	created TIMESTAMPTZ NOT NULL,

	PRIMARY KEY(poolid, transactionconfirmationdata)
);

CREATE TABLE payout_manager_ownership
(
	id SMALLINT NOT NULL PRIMARY KEY CHECK(id = 1),
	generation BIGINT NOT NULL DEFAULT 0,
	owner_id UUID NULL,
	owner_host TEXT NULL,
	owner_process_id INT NULL,
	acquired TIMESTAMPTZ NULL,
	released TIMESTAMPTZ NULL
);

INSERT INTO payout_manager_ownership(id) VALUES(1);

CREATE TABLE poolstats
(
	id BIGSERIAL NOT NULL PRIMARY KEY,
	poolid TEXT NOT NULL,
	connectedminers INT NOT NULL DEFAULT 0,
	poolhashrate DOUBLE PRECISION NOT NULL DEFAULT 0,
	sharespersecond DOUBLE PRECISION NOT NULL DEFAULT 0,
	networkhashrate DOUBLE PRECISION NOT NULL DEFAULT 0,
	networkdifficulty DOUBLE PRECISION NOT NULL DEFAULT 0,
	lastnetworkblocktime TIMESTAMPTZ NULL,
    blockheight BIGINT NOT NULL DEFAULT 0,
    connectedpeers INT NOT NULL DEFAULT 0,
	created TIMESTAMPTZ NOT NULL
);

CREATE INDEX IDX_POOLSTATS_POOL_CREATED on poolstats(poolid, created);

CREATE TABLE minerstats
(
	id BIGSERIAL NOT NULL PRIMARY KEY,
	poolid TEXT NOT NULL,
	miner TEXT NOT NULL,
	worker TEXT NOT NULL,
	sessionid TEXT NULL,
	hashrate DOUBLE PRECISION NOT NULL DEFAULT 0,
	sharespersecond DOUBLE PRECISION NOT NULL DEFAULT 0,
	created TIMESTAMPTZ NOT NULL
);

CREATE INDEX IDX_MINERSTATS_POOL_CREATED on minerstats(poolid, created);
CREATE INDEX IDX_MINERSTATS_POOL_MINER_CREATED on minerstats(poolid, miner, created);
CREATE INDEX IDX_MINERSTATS_POOL_MINER_WORKER_CREATED_HASHRATE on minerstats(poolid,miner,worker,created desc,hashrate);
CREATE INDEX IDX_MINERSTATS_POOL_MINER_WORKER_SESSION_CREATED on minerstats(poolid, miner, worker, sessionid, created desc);
CREATE INDEX IDX_MINERSTATS_POOL_MINER_SESSION_CREATED on minerstats(poolid, miner, sessionid, created desc);

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
