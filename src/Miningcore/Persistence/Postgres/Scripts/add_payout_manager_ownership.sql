-- Adds durable payout-manager ownership, payment-persistence idempotency and
-- recovery-file replay protection. Required by every payment-processing cluster,
-- even when merged mining is disabled, and by recorder/recovery-only deployments
-- that use the -rs share-recovery importer.
-- Stop every payout manager before applying this migration.

BEGIN;

CREATE TABLE IF NOT EXISTS share_recovery_imports
(
    filehash TEXT NOT NULL PRIMARY KEY,
    filename TEXT NOT NULL,
    recordcount INT NOT NULL,
    created TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS payment_batches
(
    poolid TEXT NOT NULL,
    transactionconfirmationdata TEXT NOT NULL,
    created TIMESTAMPTZ NOT NULL,

    PRIMARY KEY(poolid, transactionconfirmationdata)
);

-- Protect database retries of payments already recorded before this migration.
INSERT INTO payment_batches(poolid, transactionconfirmationdata, created)
SELECT poolid, transactionconfirmationdata, MIN(created)
FROM payments
GROUP BY poolid, transactionconfirmationdata
ON CONFLICT(poolid, transactionconfirmationdata) DO NOTHING;

CREATE TABLE IF NOT EXISTS payout_manager_ownership
(
    id SMALLINT NOT NULL PRIMARY KEY CHECK(id = 1),
    generation BIGINT NOT NULL DEFAULT 0,
    owner_id UUID NULL,
    owner_host TEXT NULL,
    owner_process_id INT NULL,
    acquired TIMESTAMPTZ NULL,
    released TIMESTAMPTZ NULL
);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace ns ON ns.oid = rel.relnamespace
        WHERE ns.nspname = current_schema()
          AND rel.relname = 'payment_batches'
          AND con.contype = 'p'
          AND pg_get_constraintdef(con.oid) ILIKE
              'PRIMARY KEY (poolid, transactionconfirmationdata)%'
    ) THEN
        RAISE EXCEPTION 'payment_batches must have PRIMARY KEY(poolid, transactionconfirmationdata); rename or repair the stale table before rerunning this migration';
    END IF;
END $$;

INSERT INTO payout_manager_ownership(id) VALUES(1)
ON CONFLICT(id) DO NOTHING;

COMMIT;

-- An ownership row is cleared automatically only after a clean Miningcore stop.
-- After an unclean stop, first prove that the previous process is no longer alive
-- and reconcile wallet history for any payout that may have been submitted but not
-- persisted. Only then release the stale marker explicitly:
--
-- UPDATE payout_manager_ownership
-- SET owner_id = NULL, owner_host = NULL, owner_process_id = NULL, released = now()
-- WHERE id = 1;
