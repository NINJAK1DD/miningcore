\set ON_ERROR_STOP on

BEGIN;
INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type,
    transactionconfirmationdata, miner, hash, created)
VALUES (:'pool', 1, 1, 'pending', 'auxpow',
    'auxpow-block:race-child', 'direct-miner', 'race-child', now())
ON CONFLICT (poolid, hash) WHERE type = 'auxpow' DO NOTHING;
COMMIT;
