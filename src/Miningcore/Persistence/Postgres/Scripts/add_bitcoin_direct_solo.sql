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
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directsubmissionstate TEXT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directsubmissionblock TEXT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directsubmissionattempts INT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directsubmissiondefinitivemisses INT NULL;
ALTER TABLE blocks ADD COLUMN IF NOT EXISTS directsubmissionlastattempt TIMESTAMPTZ NULL;

-- Earlier pre-release builds reused the historical PPS candidate type. Move only
-- rows carrying the complete direct-settlement marker to the dedicated identity.
-- Drop the compatibility trigger first because it deliberately rejects generic
-- updates to these rows; the whole migration remains transactional.
DROP TRIGGER IF EXISTS trg_guard_bitcoin_direct_block_update ON blocks;
DROP TRIGGER IF EXISTS trg_clear_bitcoin_direct_block_update_guard ON blocks;

-- Rebuild the named contract so reapplying this migration repairs a partially
-- applied or locally weakened constraint instead of accepting it by name.
ALTER TABLE blocks DROP CONSTRAINT IF EXISTS
    chk_blocks_bitcoin_direct_settlement;
UPDATE blocks SET type = 'bitcoin-coinbase-direct'
    WHERE settlementmode = 'coinbase-direct' AND type = 'bitcoin-direct';
-- Rows produced by an earlier pre-release build cannot be made replayable because
-- their exact serialized block was never stored. Preserve them under an explicit
-- audit-only compatibility state; every newly inserted row must carry an outbox payload.
UPDATE blocks SET
        directsubmissionstate = 'legacy-observed',
        directsubmissionattempts = 0,
        directsubmissiondefinitivemisses = 0
    WHERE settlementmode = 'coinbase-direct'
      AND type = 'bitcoin-coinbase-direct'
      AND directsubmissionstate IS NULL;
ALTER TABLE blocks ADD CONSTRAINT chk_blocks_bitcoin_direct_settlement
        CHECK (
            (num_nonnulls(settlementmode, grossrewardsatoshis,
                directminerrewardsatoshis, directminerscriptpubkey,
                directrecipientoutputs, directsubmissionstate,
                directsubmissionblock, directsubmissionattempts,
                directsubmissiondefinitivemisses,
                directsubmissionlastattempt) = 0 AND
                directsettlementlastchecked IS NULL AND
                type IS DISTINCT FROM 'bitcoin-coinbase-direct')
            OR
            (num_nonnulls(settlementmode, grossrewardsatoshis,
                directminerrewardsatoshis, directminerscriptpubkey,
                directrecipientoutputs) = 5 AND
                settlementmode = 'coinbase-direct' AND
                type = 'bitcoin-coinbase-direct' AND
                grossrewardsatoshis > 0 AND
                directminerrewardsatoshis > 0 AND
                directminerrewardsatoshis <= grossrewardsatoshis AND
                directminerscriptpubkey ~ '^[0-9a-f]+$' AND
                length(directminerscriptpubkey) % 2 = 0 AND
                jsonb_typeof(directrecipientoutputs) = 'array' AND
                (
                    (directsubmissionstate = 'legacy-observed' AND
                        directsubmissionblock IS NULL AND
                        directsubmissionattempts = 0 AND
                        directsubmissiondefinitivemisses = 0 AND
                        directsubmissionlastattempt IS NULL)
                    OR
                    (directsubmissionstate IN ('prepared',
                            'submitted-uncertain', 'observed-active', 'rejected') AND
                        directsubmissionblock ~ '^[0-9a-f]+$' AND
                        length(directsubmissionblock) BETWEEN 162 AND 8000000 AND
                        length(directsubmissionblock) % 2 = 0 AND
                        directsubmissionattempts >= 0 AND
                        directsubmissiondefinitivemisses >= 0 AND
                        directsubmissiondefinitivemisses <=
                            directsubmissionattempts AND
                        ((directsubmissionstate = 'prepared' AND
                            directsubmissionattempts = 0 AND
                            directsubmissiondefinitivemisses = 0 AND
                            directsubmissionlastattempt IS NULL AND
                            status = 'pending') OR
                         (directsubmissionstate <> 'prepared' AND
                            directsubmissionattempts > 0 AND
                            directsubmissionlastattempt IS NOT NULL)) AND
                        (directsubmissionstate <> 'submitted-uncertain' OR
                            status = 'pending') AND
                        (directsubmissionstate <> 'rejected' OR
                            (status = 'orphaned' AND
                             directsubmissiondefinitivemisses >= 3))))
            )
        ) NOT VALID;

ALTER TABLE blocks VALIDATE CONSTRAINT
    chk_blocks_bitcoin_direct_settlement;

DROP INDEX IF EXISTS idx_blocks_bitcoin_coinbase_direct_pool_hash;
CREATE UNIQUE INDEX idx_blocks_bitcoin_coinbase_direct_pool_hash
    ON blocks(poolid, hash)
    WHERE type = 'bitcoin-coinbase-direct';

-- Rebuild the named partial index so the periodic post-maturity scan remains
-- ordered, bounded and repairable when a local schema drifted.
DROP INDEX IF EXISTS idx_blocks_bitcoin_direct_reconcile;
CREATE INDEX idx_blocks_bitcoin_direct_reconcile ON blocks(
    poolid, directsettlementlastchecked ASC NULLS FIRST, created, id,
    blockheight)
    WHERE status IN ('confirmed', 'orphaned') AND
        type = 'bitcoin-coinbase-direct' AND
        settlementmode = 'coinbase-direct';

DROP INDEX IF EXISTS idx_blocks_bitcoin_direct_submission;
CREATE INDEX idx_blocks_bitcoin_direct_submission ON blocks(poolid, id)
    WHERE status = 'pending' AND type = 'bitcoin-coinbase-direct' AND
        settlementmode = 'coinbase-direct' AND
        directsubmissionstate IN ('prepared', 'submitted-uncertain');

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

CREATE TRIGGER trg_guard_bitcoin_direct_block_update
    BEFORE UPDATE ON blocks
    FOR EACH ROW
    EXECUTE FUNCTION guard_bitcoin_direct_block_update();

-- The authorisation GUC is transaction-local, so clear it after every UPDATE
-- statement. This prevents one audited repository statement from authorising a
-- later generic statement in the same transaction.
CREATE OR REPLACE FUNCTION clear_bitcoin_direct_block_update_guard()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog
AS $$
BEGIN
    PERFORM set_config('miningcore.direct_settlement_update', 'off', true);
    RETURN NULL;
END;
$$;

CREATE TRIGGER trg_clear_bitcoin_direct_block_update_guard
    AFTER UPDATE ON blocks
    FOR EACH STATEMENT
    EXECUTE FUNCTION clear_bitcoin_direct_block_update_guard();

COMMIT;
