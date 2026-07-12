\set ON_ERROR_STOP on

BEGIN;
UPDATE blocks
SET type = 'auxpow',
    transactionconfirmationdata = 'race-coinbase'
WHERE poolid = :'pool'
  AND hash = 'race-child'
  AND type = 'auxpow-claim'
  AND NOT EXISTS (
      SELECT 1 FROM blocks finalized
      WHERE finalized.poolid = :'pool'
        AND finalized.hash = 'race-child'
        AND finalized.type = 'auxpow'
        AND finalized.id <> blocks.id
  );
COMMIT;
