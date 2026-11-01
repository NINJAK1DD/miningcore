-- ADMINISTRATOR ACTION, NOT A MIGRATION. Stop the pool and complete the no-broadcast audit in the
-- Pearl operator guide first. This script cannot prove absence of a transaction.
-- psql -v pool=... -v marker=... -v audit_sha256=... -v reason=...
--      -v acknowledge=NO_TRANSACTION_CREATED -f release_pearl_payout_intent.sql
\set ON_ERROR_STOP on
BEGIN;
SELECT set_config('miningcore.release_pool', :'pool', true),
       set_config('miningcore.release_marker', :'marker', true),
       set_config('miningcore.release_audit', :'audit_sha256', true),
       set_config('miningcore.release_reason', :'reason', true),
       set_config('miningcore.release_ack', :'acknowledge', true);
DO $$
DECLARE intent pearl_payout_intents%ROWTYPE;
BEGIN
    IF current_setting('miningcore.release_ack') <> 'NO_TRANSACTION_CREATED'
       OR current_setting('miningcore.release_audit') !~ '^[0-9a-f]{64}$'
       OR length(current_setting('miningcore.release_reason')) NOT BETWEEN 16 AND 2048 THEN
        RAISE EXCEPTION 'A conclusive documented no-broadcast audit is required';
    END IF;
    SELECT * INTO STRICT intent FROM pearl_payout_intents
      WHERE poolid=current_setting('miningcore.release_pool')
        AND marker=current_setting('miningcore.release_marker') FOR UPDATE;
    IF intent.transactionid IS NOT NULL THEN
        RAISE EXCEPTION 'Known transaction identities must reconcile, never release';
    END IF;
    INSERT INTO pearl_payout_intent_releases(poolid,marker,recipients,created,releasedby,auditsha256,reason)
      VALUES(intent.poolid,intent.marker,intent.recipients,intent.created,session_user,
             current_setting('miningcore.release_audit'),current_setting('miningcore.release_reason'));
    DELETE FROM pearl_payout_intents WHERE poolid=intent.poolid AND marker=intent.marker;
END $$;
COMMIT;
