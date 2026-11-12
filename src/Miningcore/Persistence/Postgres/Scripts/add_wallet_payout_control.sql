-- Run ONCE as a separate trusted administrator, with the application's search_path.
-- Stop every wallet-capable process first. This database-wide protocol starts PAUSED.
-- Deliberately fail on an existing schema: never reset a stop or adopt lookalike storage.
-- First set miningcore.wallet_control_app_role to the exact persistence.postgres.user.
BEGIN;
SET LOCAL ROLE NONE;
DO $migration$
DECLARE app_role name;
BEGIN
    IF NOT pg_try_advisory_xact_lock(19779,5259609) THEN
        RAISE EXCEPTION 'Stop payout managers before installing wallet control';
    END IF;
    SELECT rolname INTO STRICT app_role FROM pg_roles
        WHERE rolname=current_setting('miningcore.wallet_control_app_role');
    IF app_role = session_user OR pg_has_role(app_role, session_user, 'MEMBER')
        OR EXISTS(SELECT FROM pg_roles WHERE rolname=app_role
            AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)) THEN
        RAISE EXCEPTION 'Use a separate administrator that the application cannot assume';
    END IF;
END $migration$;

CREATE SCHEMA miningcore_payout_control;
REVOKE ALL ON SCHEMA miningcore_payout_control FROM PUBLIC;
CREATE TABLE miningcore_payout_control.state (
    id integer PRIMARY KEY CHECK(id=1),
    protocol_version integer NOT NULL CHECK(protocol_version=1),
    revision bigint NOT NULL CHECK(revision>0),
    paused boolean NOT NULL
);
INSERT INTO miningcore_payout_control.state VALUES(1,1,1,true);
CREATE TABLE miningcore_payout_control.batches (
    batch_id uuid PRIMARY KEY,
    owner_id uuid NOT NULL,
    generation bigint NOT NULL CHECK(generation>0),
    pool_id text NOT NULL CHECK(length(pool_id) BETWEEN 1 AND 200),
    payout_digest text NOT NULL CHECK(payout_digest ~ '^[0-9a-f]{64}$'),
    admitted timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed timestamptz,
    resolution text CHECK(resolution IN ('completed','reconciled')),
    CHECK((completed IS NULL) = (resolution IS NULL))
);
CREATE INDEX ON miningcore_payout_control.batches(admitted) WHERE completed IS NULL;
CREATE TABLE miningcore_payout_control.events (
    revision bigint PRIMARY KEY,
    occurred timestamptz NOT NULL DEFAULT clock_timestamp(),
    database_role text NOT NULL DEFAULT session_user,
    action text NOT NULL CHECK(action IN ('initialize','pause','resume','reconcile')),
    incident text NOT NULL CHECK(length(btrim(incident)) BETWEEN 1 AND 200),
    batch_id uuid
);
INSERT INTO miningcore_payout_control.events(revision,action,incident)
    VALUES(1,'initialize','Control installed paused; coordinated upgrade required');

CREATE FUNCTION miningcore_payout_control.protect_events() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN RAISE EXCEPTION 'Wallet control audit history is append-only' USING ERRCODE='55000'; END $$;
CREATE TRIGGER append_only BEFORE UPDATE OR DELETE OR TRUNCATE
    ON miningcore_payout_control.events FOR EACH STATEMENT
    EXECUTE FUNCTION miningcore_payout_control.protect_events();

-- Only the backend holding the existing payout advisory lock may admit/complete.
-- Do not query application-owned tables inside SECURITY DEFINER functions.
CREATE FUNCTION miningcore_payout_control.require_owner_lock() RETURNS void
LANGUAGE plpgsql SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    IF NOT EXISTS(SELECT FROM pg_locks WHERE locktype='advisory'
        AND pid=pg_backend_pid() AND database=(SELECT oid FROM pg_database WHERE datname=current_database())
        AND classid=19779 AND objid=5259609 AND objsubid=2 AND mode='ExclusiveLock' AND granted) THEN
        RAISE EXCEPTION 'The payout ownership advisory lock is required' USING ERRCODE='55000';
    END IF;
END $$;

CREATE FUNCTION miningcore_payout_control.begin_batch(token uuid, owner_token uuid, owner_generation bigint,
    pool text, snapshot_digest text)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp SET lock_timeout='2s' AS $$
DECLARE control miningcore_payout_control.state;
BEGIN
    PERFORM miningcore_payout_control.require_owner_lock();
    SELECT * INTO STRICT control FROM miningcore_payout_control.state WHERE id=1 FOR UPDATE;
    IF control.protocol_version<>1 THEN RAISE EXCEPTION 'Unsupported wallet control protocol'; END IF;
    IF control.paused THEN RETURN false; END IF;
    IF EXISTS(SELECT FROM miningcore_payout_control.batches WHERE completed IS NULL) THEN
        RAISE EXCEPTION 'An outstanding wallet batch requires completion or explicit reconciliation' USING ERRCODE='55000';
    END IF;
    INSERT INTO miningcore_payout_control.batches(batch_id,owner_id,generation,pool_id,payout_digest)
        VALUES(token,owner_token,owner_generation,pool,snapshot_digest);
    RETURN true;
END $$;

CREATE FUNCTION miningcore_payout_control.finish_batch(token uuid, owner_token uuid, owner_generation bigint)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
BEGIN
    PERFORM miningcore_payout_control.require_owner_lock();
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

CREATE FUNCTION miningcore_payout_control.set_paused(stop boolean, expected_revision bigint, incident_reference text)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE control miningcore_payout_control.state;
BEGIN
    IF stop IS NULL OR expected_revision IS NULL OR incident_reference IS NULL
        OR length(btrim(incident_reference)) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'State, expected revision and incident reference are required';
    END IF;
    SELECT * INTO STRICT control FROM miningcore_payout_control.state WHERE id=1 FOR UPDATE;
    IF control.protocol_version<>1 OR control.revision<>expected_revision THEN
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

-- Explicit operator reconciliation only. Holding the lock proves no *connected*
-- payout manager holds it; the operator must ALSO establish process death and wallet history.
CREATE FUNCTION miningcore_payout_control.reconcile_batch(token uuid, expected_revision bigint, incident_reference text)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,pg_temp AS $$
DECLARE control miningcore_payout_control.state;
BEGIN
    PERFORM miningcore_payout_control.require_owner_lock();
    IF token IS NULL OR expected_revision IS NULL OR incident_reference IS NULL
        OR length(btrim(incident_reference)) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'Batch identity, expected revision and incident reference are required';
    END IF;
    SELECT * INTO STRICT control FROM miningcore_payout_control.state WHERE id=1 FOR UPDATE;
    IF control.protocol_version<>1 OR NOT control.paused OR control.revision<>expected_revision THEN
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

-- Remove all default privileges, including non-PUBLIC defaults, before granting
-- only runtime read/admit/complete privileges. Operator grants are a separate step.
CREATE FUNCTION miningcore_payout_control.runtime_role_safe(app_role name)
RETURNS boolean LANGUAGE sql SET search_path=pg_catalog,pg_temp AS $$
    SELECT EXISTS(SELECT FROM pg_roles WHERE rolname=app_role)
        AND NOT EXISTS(
            SELECT FROM pg_roles r WHERE (r.rolname=app_role OR pg_has_role(app_role,r.oid,'MEMBER'))
            AND (r.rolsuper OR r.rolcreaterole OR r.rolcreatedb OR r.rolreplication OR r.rolbypassrls
                OR r.rolname IN ('pg_execute_server_program','pg_read_server_files','pg_write_server_files',
                    'pg_signal_backend','pg_checkpoint','pg_maintain')
                OR has_schema_privilege(r.oid,'miningcore_payout_control','CREATE')
                OR EXISTS(SELECT FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
                    WHERE n.nspname='miningcore_payout_control' AND t.relkind='r'
                    AND has_table_privilege(r.oid,t.oid,'INSERT,UPDATE,DELETE,TRUNCATE,TRIGGER,REFERENCES'))
                OR has_function_privilege(r.oid,'miningcore_payout_control.set_paused(boolean,bigint,text)','EXECUTE')
                OR has_function_privilege(r.oid,'miningcore_payout_control.reconcile_batch(uuid,bigint,text)','EXECUTE')))
$$;

DO $permissions$
DECLARE app_role name; grantee name;
BEGIN
    FOR grantee IN SELECT rolname FROM pg_roles WHERE rolname<>session_user LOOP
        EXECUTE format('REVOKE ALL ON SCHEMA miningcore_payout_control FROM %I',grantee);
        EXECUTE format('REVOKE ALL ON ALL TABLES IN SCHEMA miningcore_payout_control FROM %I',grantee);
        EXECUTE format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA miningcore_payout_control FROM %I',grantee);
    END LOOP;
    REVOKE ALL ON ALL TABLES IN SCHEMA miningcore_payout_control FROM PUBLIC;
    REVOKE ALL ON ALL FUNCTIONS IN SCHEMA miningcore_payout_control FROM PUBLIC;
    SELECT rolname INTO STRICT app_role FROM pg_roles
        WHERE rolname=current_setting('miningcore.wallet_control_app_role');
    EXECUTE format('GRANT USAGE ON SCHEMA miningcore_payout_control TO %I',app_role);
    EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA miningcore_payout_control TO %I',app_role);
    EXECUTE format('GRANT EXECUTE ON FUNCTION miningcore_payout_control.begin_batch(uuid,uuid,bigint,text,text), miningcore_payout_control.finish_batch(uuid,uuid,bigint), miningcore_payout_control.runtime_role_safe(name) TO %I',app_role);
    IF NOT miningcore_payout_control.runtime_role_safe(app_role) THEN
        RAISE EXCEPTION 'Runtime role has unsafe effective control privileges (including inherited or assumable roles)';
    END IF;
END $permissions$;
COMMIT;
