-- Required before enabling Litecoin-Dogecoin merged mining on an existing database.
-- The partial index leaves block types used by other coin families unchanged.
UPDATE blocks
SET type = 'auxpow'
WHERE type IS NULL
  AND (transactionconfirmationdata LIKE 'auxpow-block:%'
    OR transactionconfirmationdata LIKE 'auxpow-uncertain:%');

CREATE UNIQUE INDEX IF NOT EXISTS IDX_BLOCKS_AUXPOW_POOL_HASH
    ON blocks(poolid, hash)
    WHERE type = 'auxpow';
