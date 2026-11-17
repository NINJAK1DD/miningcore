-- Upgrade an existing protocol-1 installation as its separate trusted owner.
-- Stop all wallet-capable processes and pause the control first. Never discard
-- outstanding batches: their sessions cannot be established retroactively.
-- Set miningcore.wallet_control_app_role to the exact runtime login first.
-- The owner must be a superuser or inherit pg_read_all_stats for session binding.
BEGIN;
SET LOCAL ROLE NONE;
DO $upgrade$
DECLARE app_role name; control miningcore_payout_control.state;
BEGIN
    IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname=session_user AND rolsuper)
        AND NOT pg_has_role(session_user,'pg_read_all_stats','USAGE') THEN
        RAISE EXCEPTION 'Wallet control owner requires effective pg_read_all_stats privileges (or superuser) for session binding' USING ERRCODE='55000';
    END IF;
    IF NOT pg_try_advisory_xact_lock(19779,5259609) THEN
        RAISE EXCEPTION 'Stop payout managers before upgrading wallet control';
    END IF;
    IF NOT EXISTS(SELECT FROM pg_namespace WHERE nspname='miningcore_payout_control'
        AND nspowner=(SELECT oid FROM pg_roles WHERE rolname=session_user))
        OR EXISTS(SELECT FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
            WHERE n.nspname='miningcore_payout_control' AND t.relkind='r'
                AND t.relowner<>(SELECT oid FROM pg_roles WHERE rolname=session_user))
        OR EXISTS(SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='miningcore_payout_control'
                AND p.proowner<>(SELECT oid FROM pg_roles WHERE rolname=session_user)) THEN
        RAISE EXCEPTION 'Run the upgrade as the trusted wallet control schema and object owner';
    END IF;
    SELECT rolname INTO STRICT app_role FROM pg_roles
        WHERE rolname=current_setting('miningcore.wallet_control_app_role');
    IF app_role=session_user OR pg_has_role(app_role,session_user,'MEMBER')
        OR NOT miningcore_payout_control.runtime_role_safe(app_role) THEN
        RAISE EXCEPTION 'Runtime role has unsafe effective control privileges';
    END IF;
    SELECT * INTO STRICT control FROM miningcore_payout_control.state WHERE id=1 FOR UPDATE;
    IF control.protocol_version<>1 OR NOT control.paused THEN
        RAISE EXCEPTION 'Upgrade requires protocol 1 paused after coordinated shutdown';
    END IF;
END $upgrade$;

ALTER TABLE miningcore_payout_control.batches
    ADD COLUMN admitting_pid integer,
    ADD COLUMN admitting_backend_start timestamptz,
    ADD CHECK((admitting_pid IS NULL AND admitting_backend_start IS NULL)
        OR (admitting_pid > 0 AND admitting_pid IS NOT NULL AND admitting_backend_start IS NOT NULL));
-- Leave legacy identities NULL: only explicit operator reconciliation may resolve
-- outstanding protocol-1 batches. Completed history and existing ACLs are retained.
ALTER TABLE miningcore_payout_control.state DROP CONSTRAINT state_protocol_version_check;
ALTER TABLE miningcore_payout_control.state ADD CHECK(protocol_version=2) NOT VALID;
UPDATE miningcore_payout_control.state SET protocol_version=2,revision=revision+1 WHERE id=1;
ALTER TABLE miningcore_payout_control.state VALIDATE CONSTRAINT state_protocol_version_check;
ALTER TABLE miningcore_payout_control.events DROP CONSTRAINT events_action_check;
ALTER TABLE miningcore_payout_control.events ADD CHECK(action IN ('initialize','pause','resume','reconcile','upgrade'));
INSERT INTO miningcore_payout_control.events(revision,action,incident)
    SELECT revision,'upgrade','Protocol 2 installed paused; original sessions required for completion'
    FROM miningcore_payout_control.state WHERE id=1;

CREATE OR REPLACE FUNCTION miningcore_payout_control.begin_batch(token uuid, owner_token uuid, owner_generation bigint,
    pool text, snapshot_digest text)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp SET lock_timeout='2s' AS $$
DECLARE control miningcore_payout_control.state; session_start timestamptz;
BEGIN
    PERFORM miningcore_payout_control.require_owner_lock();
    SELECT * INTO STRICT control FROM miningcore_payout_control.state WHERE id=1 FOR UPDATE;
    IF control.protocol_version<>2 THEN RAISE EXCEPTION 'Unsupported wallet control protocol'; END IF;
    IF control.paused THEN RETURN false; END IF;
    IF EXISTS(SELECT FROM miningcore_payout_control.batches WHERE completed IS NULL) THEN
        RAISE EXCEPTION 'An outstanding wallet batch requires completion or explicit reconciliation' USING ERRCODE='55000';
    END IF;
    SELECT backend_start INTO STRICT session_start FROM pg_stat_activity WHERE pid=pg_backend_pid();
    IF session_start IS NULL THEN
        RAISE EXCEPTION 'Wallet control owner cannot read backend_start; restore effective pg_read_all_stats privileges' USING ERRCODE='55000';
    END IF;
    INSERT INTO miningcore_payout_control.batches(batch_id,owner_id,generation,pool_id,payout_digest,
        admitting_pid,admitting_backend_start)
        VALUES(token,owner_token,owner_generation,pool,snapshot_digest,pg_backend_pid(),session_start);
    RETURN true;
END $$;

CREATE OR REPLACE FUNCTION miningcore_payout_control.finish_batch(token uuid, owner_token uuid, owner_generation bigint)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE session_start timestamptz;
BEGIN
    PERFORM miningcore_payout_control.require_owner_lock();
    SELECT backend_start INTO STRICT session_start FROM pg_stat_activity WHERE pid=pg_backend_pid();
    IF session_start IS NULL THEN
        RAISE EXCEPTION 'Wallet control owner cannot read backend_start; restore effective pg_read_all_stats privileges' USING ERRCODE='55000';
    END IF;
    -- A replacement runtime session may read all identities and acquire the lock,
    -- but cannot complete an unresolved batch. PID alone is unsafe after reuse.
    IF NOT EXISTS(SELECT FROM miningcore_payout_control.batches
        WHERE batch_id=token AND owner_id=owner_token AND generation=owner_generation
            AND admitting_pid=pg_backend_pid() AND admitting_backend_start=session_start) THEN
        RAISE EXCEPTION 'Wallet batch completion requires its admitting PostgreSQL session' USING ERRCODE='55000';
    END IF;
    -- Completion does not take the operator's state lock. A concurrent resume
    -- may conservatively see the batch as outstanding and refuse. Reconciliation
    -- requires our advisory lock, so it cannot race this update.
    UPDATE miningcore_payout_control.batches SET completed=clock_timestamp(),resolution='completed'
        WHERE batch_id=token AND owner_id=owner_token AND generation=owner_generation AND completed IS NULL;
    IF NOT FOUND AND NOT EXISTS(SELECT FROM miningcore_payout_control.batches
        WHERE batch_id=token AND owner_id=owner_token AND generation=owner_generation
            AND resolution='completed' AND completed IS NOT NULL) THEN
        RAISE EXCEPTION 'Wallet batch identity does not match';
    END IF;
END $$;

CREATE OR REPLACE FUNCTION miningcore_payout_control.set_paused(stop boolean, expected_revision bigint, incident_reference text)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE control miningcore_payout_control.state;
BEGIN
    IF stop IS NULL OR expected_revision IS NULL OR incident_reference IS NULL
        OR length(btrim(incident_reference)) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'State, expected revision and incident reference are required';
    END IF;
    SELECT * INTO STRICT control FROM miningcore_payout_control.state WHERE id=1 FOR UPDATE;
    IF control.protocol_version<>2 OR control.revision<>expected_revision THEN
        RAISE EXCEPTION 'Wallet control protocol or revision changed; read state again' USING ERRCODE='40001';
    END IF;
    IF NOT stop AND EXISTS(SELECT FROM miningcore_payout_control.batches WHERE completed IS NULL) THEN
        RAISE EXCEPTION 'Cannot resume with outstanding wallet batches' USING ERRCODE='55000';
    END IF;
    UPDATE miningcore_payout_control.state SET paused=stop,revision=revision+1 WHERE id=1;
    INSERT INTO miningcore_payout_control.events(revision,action,incident)
        VALUES(control.revision+1,CASE WHEN stop THEN 'pause' ELSE 'resume' END,incident_reference);
    RETURN control.revision+1;
END $$;

CREATE OR REPLACE FUNCTION miningcore_payout_control.reconcile_batch(token uuid, expected_revision bigint, incident_reference text)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE control miningcore_payout_control.state;
BEGIN
    PERFORM miningcore_payout_control.require_owner_lock();
    IF token IS NULL OR expected_revision IS NULL OR incident_reference IS NULL
        OR length(btrim(incident_reference)) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'Batch identity, expected revision and incident reference are required';
    END IF;
    SELECT * INTO STRICT control FROM miningcore_payout_control.state WHERE id=1 FOR UPDATE;
    IF control.protocol_version<>2 OR NOT control.paused OR control.revision<>expected_revision THEN
        RAISE EXCEPTION 'Reconciliation requires paused state and the current revision' USING ERRCODE='40001';
    END IF;
    UPDATE miningcore_payout_control.batches SET completed=clock_timestamp(),resolution='reconciled'
        WHERE batch_id=token AND completed IS NULL;
    IF NOT FOUND THEN RAISE EXCEPTION 'Outstanding wallet batch does not match'; END IF;
    UPDATE miningcore_payout_control.state SET revision=revision+1 WHERE id=1;
    INSERT INTO miningcore_payout_control.events(revision,action,incident,batch_id)
        VALUES(control.revision+1,'reconcile',incident_reference,token);
    RETURN control.revision+1;
END $$;
COMMIT;
