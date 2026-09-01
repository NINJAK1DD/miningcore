-- Add immutable Bitcoin direct-SOLO coinbase settlement evidence.
-- Additive and idempotent: existing custodial blocks remain NULL and retain
-- their historical wallet/balance settlement path.
-- Stop every Miningcore writer and take a verified backup before applying it.
\set ON_ERROR_STOP on

BEGIN;

ALTER TABLE blocks ADD COLUMN IF NOT EXISTS settlementmode TEXT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS grossrewardsatoshis BIGINT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directminerrewardsatoshis BIGINT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directminerscriptpubkey TEXT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directrecipientoutputs JSONB NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directsettlementlastchecked TIMESTAMPTZ NULL;

-- Rebuild the named contract so reapplying this migration repairs a partially
-- applied or locally weakened constraint instead of accepting it by name.
ALTER TABLE blocks DROP CONSTRAINT IF EXISTS
    chk_blocks_bitcoin_direct_settlement;
ALTER TABLE blocks ADD CONSTRAINT chk_blocks_bitcoin_direct_settlement
        CHECK (
            (num_nonnulls(settlementmode, grossrewardsatoshis,
                directminerrewardsatoshis, directminerscriptpubkey,
                directrecipientoutputs) = 0 AND
                directsettlementlastchecked IS NULL)
            OR
            (num_nonnulls(settlementmode, grossrewardsatoshis,
                directminerrewardsatoshis, directminerscriptpubkey,
                directrecipientoutputs) = 5 AND
                settlementmode = 'coinbase-direct' AND
                type = 'bitcoin-direct' AND
                grossrewardsatoshis > 0 AND
                directminerrewardsatoshis > 0 AND
                directminerrewardsatoshis <= grossrewardsatoshis AND
                directminerscriptpubkey ~ '^[0-9a-f]+$' AND
                length(directminerscriptpubkey) % 2 = 0 AND
                jsonb_typeof(directrecipientoutputs) = 'array')
        ) NOT VALID;

ALTER TABLE blocks VALIDATE CONSTRAINT
    chk_blocks_bitcoin_direct_settlement;

-- Rebuild the named partial index so the periodic post-maturity scan remains
-- ordered, bounded and repairable when a local schema drifted.
DROP INDEX IF EXISTS idx_blocks_bitcoin_direct_reconcile;
CREATE INDEX idx_blocks_bitcoin_direct_reconcile ON blocks(
    poolid, directsettlementlastchecked ASC NULLS FIRST, created, id)
    WHERE status IN ('confirmed', 'orphaned') AND type = 'bitcoin-direct' AND
        settlementmode = 'coinbase-direct';

-- A binary predating direct settlement does not understand that these rows are
-- non-custodial and must never credit balances. Refuse its generic UPDATE path.
-- Current code explicitly enables the guard only for its audited direct-row
-- update statements, within the current transaction.
CREATE OR REPLACE FUNCTION guard_bitcoin_direct_block_update()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    IF OLD.settlementmode = 'coinbase-direct' AND
       current_setting('miningcore.direct_settlement_update', true)
           IS DISTINCT FROM 'on' THEN
        RAISE EXCEPTION USING
            ERRCODE = '55000',
            MESSAGE = 'direct-settlement block updates require a compatible Miningcore binary';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_guard_bitcoin_direct_block_update ON blocks;
CREATE TRIGGER trg_guard_bitcoin_direct_block_update
    BEFORE UPDATE ON blocks
    FOR EACH ROW
    EXECUTE FUNCTION guard_bitcoin_direct_block_update();

COMMIT;
