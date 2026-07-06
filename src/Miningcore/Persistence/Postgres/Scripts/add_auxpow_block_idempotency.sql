-- Required before enabling Litecoin-Dogecoin merged mining on an existing database.
-- The partial indexes leave block types used by other coin families unchanged.
-- Every change is transactional: failed validation or index creation restores the
-- database to its exact pre-migration state.
BEGIN;

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

UPDATE blocks
SET type = 'merged-parent-uncertain'
WHERE type = 'parent-uncertain'
  AND transactionconfirmationdata LIKE 'parent-uncertain:%';

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

    IF EXISTS (
        SELECT 1 FROM blocks
        WHERE type IN ('merged-parent', 'merged-parent-uncertain')
        GROUP BY poolid, hash
        HAVING COUNT(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate merged parent rows require manual review before migration';
    END IF;
END $$;

-- Recreate every index so rerunning this migration repairs stale same-named indexes
-- left by prerelease versions instead of preserving an incompatible definition.
DROP INDEX IF EXISTS IDX_BLOCKS_AUXPOW_POOL_HASH;
CREATE UNIQUE INDEX IDX_BLOCKS_AUXPOW_POOL_HASH
    ON blocks(poolid, hash)
    WHERE type = 'auxpow';

DROP INDEX IF EXISTS IDX_BLOCKS_AUXPOW_CLAIM;
CREATE UNIQUE INDEX IDX_BLOCKS_AUXPOW_CLAIM
    ON blocks(poolid, hash,
        (regexp_replace(transactionconfirmationdata, ':[0-9]+$', '')))
    WHERE type = 'auxpow-claim';

DROP INDEX IF EXISTS IDX_BLOCKS_MERGED_PARENT_POOL_HASH;
CREATE UNIQUE INDEX IDX_BLOCKS_MERGED_PARENT_POOL_HASH
    ON blocks(poolid, hash)
    WHERE type IN ('merged-parent', 'merged-parent-uncertain');

COMMIT;
