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

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint con
        JOIN pg_class rel ON rel.oid = con.conrelid
        JOIN pg_namespace ns ON ns.oid = rel.relnamespace
        JOIN pg_index idx ON idx.indexrelid = con.conindid
        WHERE ns.nspname = current_schema()
          AND rel.relname = 'payment_batches'
          AND con.contype = 'p'
          AND NOT con.condeferrable
          AND idx.indisunique
          AND idx.indisvalid
          AND idx.indisready
          AND idx.indimmediate
          AND pg_get_constraintdef(con.oid) ILIKE
              'PRIMARY KEY (poolid, transactionconfirmationdata)%'
    ) THEN
        RAISE EXCEPTION 'payment_batches must have a non-deferrable PRIMARY KEY(poolid, transactionconfirmationdata); rename or repair the stale table before rerunning this migration';
    END IF;
END $$;

-- Protect database retries of payments already recorded before this migration. Validate the
-- arbiter first so a deferrable or otherwise malformed key produces the actionable error above
-- instead of failing inside ON CONFLICT after wallet safety has already been enabled operationally.
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

INSERT INTO payout_manager_ownership(id) VALUES(1)
ON CONFLICT(id) DO NOTHING;

-- The documented upgrade command is intentionally run by a PostgreSQL administrator so it can
-- repair installations whose existing objects have mixed ownership. Newly created relations would
-- otherwise remain owned by that administrator and the application role could not use them. Keep
-- these relations aligned with the database owner, which createdb.sql and the installation guide
-- define as the Miningcore application role.
DO $$
DECLARE
    database_owner NAME;
    target_schema NAME := current_schema();
    relation_name NAME;
BEGIN
    SELECT pg_get_userbyid(datdba)
    INTO database_owner
    FROM pg_database
    WHERE datname = current_database();

    IF database_owner IS NULL THEN
        RAISE EXCEPTION 'Could not resolve the owner of database %', current_database();
    END IF;

    FOREACH relation_name IN ARRAY ARRAY[
        'share_recovery_imports'::NAME,
        'payment_batches'::NAME,
        'payout_manager_ownership'::NAME
    ]
    LOOP
        EXECUTE format('ALTER TABLE %I.%I OWNER TO %I',
            target_schema, relation_name, database_owner);
    END LOOP;
END $$;

COMMIT;

-- An ownership row is cleared automatically only after a clean Miningcore stop.
-- After an unclean stop, first prove that the previous process is no longer alive
-- and reconcile wallet history for any payout that may have been submitted but not
-- persisted. Only then release the stale marker explicitly:
--
-- UPDATE payout_manager_ownership
-- SET owner_id = NULL, owner_host = NULL, owner_process_id = NULL, released = now()
-- WHERE id = 1;
