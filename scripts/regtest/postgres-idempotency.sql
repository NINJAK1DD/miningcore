\set ON_ERROR_STOP on

BEGIN;

INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type,
    transactionconfirmationdata, miner, hash, created)
VALUES
    ('regtest-idempotency', 1, 1, 'pending', 'auxpow',
        'auxpow-block:child-final', 'miner-a', 'child-final', now()),
    ('regtest-idempotency', 2, 1, 'pending', 'auxpow-claim',
        'auxpow-claim:child-claim:proof-a:0', 'miner-a', 'child-claim', now()),
    ('regtest-idempotency', 3, 1, 'pending', 'merged-parent',
        'coinbase-parent', 'miner-a', 'parent-block', now());

INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type,
    transactionconfirmationdata, miner, hash, created)
VALUES ('regtest-idempotency', 1, 1, 'pending', 'auxpow',
    'auxpow-block:child-final', 'miner-b', 'child-final', now())
ON CONFLICT (poolid, hash) WHERE type = 'auxpow' DO NOTHING;

INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type,
    transactionconfirmationdata, miner, hash, created)
VALUES ('regtest-idempotency', 2, 1, 'pending', 'auxpow-claim',
    'auxpow-claim:child-claim:proof-a:2', 'miner-b', 'child-claim', now())
ON CONFLICT (poolid, hash,
    (regexp_replace(transactionconfirmationdata, ':[0-9]+$', '')))
WHERE type = 'auxpow-claim' DO NOTHING;

INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type,
    transactionconfirmationdata, miner, hash, created)
VALUES ('regtest-idempotency', 3, 1, 'pending', 'merged-parent-uncertain',
    'parent-uncertain:parent-block', 'miner-b', 'parent-block', now())
ON CONFLICT (poolid, hash)
WHERE type IN ('merged-parent', 'merged-parent-uncertain') DO NOTHING;

DO $$
BEGIN
    IF (SELECT count(*) FROM blocks
        WHERE poolid = 'regtest-idempotency' AND type = 'auxpow') <> 1 THEN
        RAISE EXCEPTION 'final AuxPoW replay was not idempotent';
    END IF;

    IF (SELECT count(*) FROM blocks
        WHERE poolid = 'regtest-idempotency' AND type = 'auxpow-claim') <> 1 THEN
        RAISE EXCEPTION 'proof-specific claim replay was not idempotent';
    END IF;

    IF (SELECT count(*) FROM blocks
        WHERE poolid = 'regtest-idempotency'
          AND type IN ('merged-parent', 'merged-parent-uncertain')) <> 1 THEN
        RAISE EXCEPTION 'merged-parent replay was not idempotent';
    END IF;
END $$;

SELECT type, count(*) AS surviving_rows
FROM blocks
WHERE poolid = 'regtest-idempotency'
GROUP BY type
ORDER BY type;

ROLLBACK;
