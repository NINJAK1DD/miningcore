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
-- Resolve the schema of the same unqualified blocks relation used above and by runtime SQL.
-- An unrelated same-named index earlier in search_path must not shadow the target index.
DO $migration$
DECLARE
    blocks_schema name;
    index_name text;
BEGIN
    SELECT namespace.nspname
    INTO blocks_schema
    FROM pg_class relation
    JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
    WHERE relation.oid = to_regclass('blocks');

    IF blocks_schema IS NULL THEN
        RAISE EXCEPTION 'Unable to resolve the schema containing the active blocks table';
    END IF;

    FOREACH index_name IN ARRAY ARRAY[
        'idx_blocks_auxpow_pool_hash',
        'idx_blocks_auxpow_claim',
        'idx_blocks_merged_parent_pool_hash'
    ]
    LOOP
        EXECUTE format('DROP INDEX IF EXISTS %I.%I', blocks_schema, index_name);
    END LOOP;
END
$migration$;

CREATE UNIQUE INDEX IDX_BLOCKS_AUXPOW_POOL_HASH
    ON blocks(poolid, hash)
    WHERE type = 'auxpow';

CREATE UNIQUE INDEX IDX_BLOCKS_AUXPOW_CLAIM
    ON blocks(poolid, hash,
        (regexp_replace(transactionconfirmationdata, ':[0-9]+$', '')))
    WHERE type = 'auxpow-claim';

CREATE UNIQUE INDEX IDX_BLOCKS_MERGED_PARENT_POOL_HASH
    ON blocks(poolid, hash)
    WHERE type IN ('merged-parent', 'merged-parent-uncertain');

COMMIT;
