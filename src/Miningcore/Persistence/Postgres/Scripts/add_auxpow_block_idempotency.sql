-- Required before enabling Litecoin-Dogecoin merged mining on an existing database.
-- The partial indexes leave block types used by other coin families unchanged.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM blocks
        WHERE transactionconfirmationdata LIKE 'auxpow-uncertain:%'
    ) THEN
        RAISE EXCEPTION 'Legacy uncertain AuxPoW rows require manual review before migration';
    END IF;
END $$;

UPDATE blocks
SET type = 'auxpow'
WHERE type IS NULL
  AND transactionconfirmationdata LIKE 'auxpow-block:%';

DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM blocks
        WHERE type = 'auxpow'
        GROUP BY poolid, hash
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate finalized AuxPoW rows require manual review before migration';
    END IF;

    IF EXISTS (
        SELECT 1 FROM blocks
        WHERE type = 'auxpow-claim'
        GROUP BY poolid, hash,
            regexp_replace(transactionconfirmationdata, ':[0-9]+$', '')
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate AuxPoW claim rows require manual review before migration';
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS IDX_BLOCKS_AUXPOW_POOL_HASH
    ON blocks(poolid, hash)
    WHERE type = 'auxpow';

DROP INDEX IF EXISTS IDX_BLOCKS_AUXPOW_CLAIM;
CREATE UNIQUE INDEX IDX_BLOCKS_AUXPOW_CLAIM
    ON blocks(poolid, hash,
        (regexp_replace(transactionconfirmationdata, ':[0-9]+$', '')))
    WHERE type = 'auxpow-claim';
