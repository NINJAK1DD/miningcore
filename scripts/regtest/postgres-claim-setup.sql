\set ON_ERROR_STOP on

DELETE FROM blocks WHERE poolid = :'pool';

INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type,
    transactionconfirmationdata, miner, hash, created)
VALUES (:'pool', 1, 1, 'pending', 'auxpow-claim',
    'auxpow-claim:race-child:race-proof:0', 'claim-miner', 'race-child', now());
