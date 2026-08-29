\set ON_ERROR_STOP on

-- CAUTION: This optional multipool optimization deletes and rebuilds the shares table.
-- Stop every writer, take a verified backup and read docs/database.md before running it.
-- A successful conversion still requires one LIST partition for every enabled pool ID before
-- Miningcore is restarted or recovery data is imported. Example:
--
--   SET ROLE miningcore;
--   CREATE TABLE public.shares_ltc1_solo
--     PARTITION OF public.shares FOR VALUES IN ('ltc1-solo');
--   RESET ROLE;

BEGIN;

SET ROLE miningcore;

DROP TABLE shares;

CREATE TABLE shares
(
	poolid TEXT NOT NULL,
	blockheight BIGINT NOT NULL,
	difficulty DOUBLE PRECISION NOT NULL,
	networkdifficulty DOUBLE PRECISION NOT NULL,
	miner TEXT NOT NULL,
	worker TEXT NULL,
	useragent TEXT NULL,
	ipaddress TEXT NOT NULL,
	source TEXT NULL,
	sharedifficulty DOUBLE PRECISION NULL,
	actualdifficulty DOUBLE PRECISION NULL,
	sessionid TEXT NULL,
	accountingid UUID NULL,
	accountingrole SMALLINT NULL,
	rewardbasissatoshis BIGINT NULL,
	created TIMESTAMP WITH TIME ZONE NOT NULL,
	CONSTRAINT CK_SHARES_ACCOUNTING_TUPLE CHECK(
		(accountingid IS NULL AND accountingrole IS NULL AND rewardbasissatoshis IS NULL)
		OR (accountingid IS NOT NULL AND accountingrole IN (1, 2, 3)
			AND rewardbasissatoshis > 0)),
	CONSTRAINT FK_SHARES_ACCOUNTING_GROUP FOREIGN KEY(accountingid)
		REFERENCES share_accounting_groups(accountingid)
) PARTITION BY LIST (poolid);

CREATE INDEX IDX_SHARES_CREATED ON shares(created);
CREATE INDEX IDX_SHARES_MINER_DIFFICULTY ON shares(miner, difficulty);
CREATE INDEX IDX_SHARES_MINER_SHAREDIFFICULTY ON shares(miner, sharedifficulty);
CREATE INDEX IDX_SHARES_MINER_ACTUALDIFFICULTY ON shares(miner, actualdifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_WORKER_ACTUALDIFFICULTY ON shares(poolid, miner, worker, actualdifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_SESSION_CREATED ON shares(poolid, miner, sessionid, created DESC);
CREATE INDEX IDX_SHARES_POOL_MINER_WORKER_SESSION_CREATED ON shares(poolid, miner, worker, sessionid, created DESC);
CREATE UNIQUE INDEX IDX_SHARES_POOL_ACCOUNTING ON shares(poolid, accountingid)
    WHERE accountingid IS NOT NULL;

RESET ROLE;

COMMIT;
