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
	created TIMESTAMP WITH TIME ZONE NOT NULL
) PARTITION BY LIST (poolid);

CREATE INDEX IDX_SHARES_CREATED ON shares(created);
CREATE INDEX IDX_SHARES_MINER_DIFFICULTY ON shares(miner, difficulty);
CREATE INDEX IDX_SHARES_MINER_SHAREDIFFICULTY ON shares(miner, sharedifficulty);
CREATE INDEX IDX_SHARES_MINER_ACTUALDIFFICULTY ON shares(miner, actualdifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_WORKER_ACTUALDIFFICULTY ON shares(poolid, miner, worker, actualdifficulty);
CREATE INDEX IDX_SHARES_POOL_MINER_SESSION_CREATED ON shares(poolid, miner, sessionid, created DESC);
CREATE INDEX IDX_SHARES_POOL_MINER_WORKER_SESSION_CREATED ON shares(poolid, miner, worker, sessionid, created DESC);

