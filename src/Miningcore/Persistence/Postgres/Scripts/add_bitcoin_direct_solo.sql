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

-- Rebuild the named contract so reapplying this migration repairs a partially
-- applied or locally weakened constraint instead of accepting it by name.
ALTER TABLE blocks DROP CONSTRAINT IF EXISTS
    chk_blocks_bitcoin_direct_settlement;
ALTER TABLE blocks ADD CONSTRAINT chk_blocks_bitcoin_direct_settlement
        CHECK (
            (settlementmode IS NULL AND grossrewardsatoshis IS NULL AND
                directminerrewardsatoshis IS NULL AND
                directminerscriptpubkey IS NULL AND
                directrecipientoutputs IS NULL)
            OR
            (settlementmode = 'coinbase-direct' AND
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

COMMIT;
