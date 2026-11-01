-- Apply as the schema owner; grant application SELECT/INSERT/UPDATE/DELETE as for other pool tables.
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
