-- Stop payout managers. Run as a separate administrator with the application's search_path.
-- Existing owner markers, payments and audit event identities are preserved.
BEGIN;
SET LOCAL ROLE NONE;
CREATE INDEX IF NOT EXISTS idx_payment_batches_recovery_created
    ON payment_batches(created DESC, poolid, transactionconfirmationdata);
DO $migration$
DECLARE
    ownership oid := 'payout_manager_ownership'::regclass;
    ownership_schema name;
    audit_schema name := 'miningcore_audit_' || ownership::text;
    app_role name;
    admin_role name := session_user;
    grantee name;
    entry record;
    signature text;
BEGIN
    SELECT n.nspname, pg_get_userbyid(c.relowner) INTO ownership_schema, app_role
        FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.oid=ownership;
    IF ownership_schema <> current_schema() THEN
        RAISE EXCEPTION 'Set search_path to the ownership schema before migrating payout recovery';
    END IF;
    IF app_role=admin_role AND NOT EXISTS(SELECT FROM pg_roles WHERE rolname=admin_role AND rolsuper)
       OR app_role<>admin_role AND pg_has_role(app_role, admin_role, 'MEMBER') THEN
        RAISE EXCEPTION 'Run as a separate administrator whose role is not inherited by the application';
    END IF;
    IF NOT EXISTS (
        SELECT FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_am a ON a.oid=c.relam
        WHERE i.indexrelid=to_regclass(format('%I.idx_payment_batches_recovery_created', ownership_schema))
          AND i.indrelid=to_regclass(format('%I.payment_batches', ownership_schema))
          AND a.amname='btree' AND i.indisvalid AND i.indisready
          AND i.indnkeyatts=3 AND i.indnatts=3 AND i.indexprs IS NULL AND i.indpred IS NULL
          AND pg_get_indexdef(i.indexrelid,1,true)='created'
          AND i.indoption[0]=3 AND i.indoption[1]=0 AND i.indoption[2]=0
          AND pg_get_indexdef(i.indexrelid,2,true)='poolid'
          AND pg_get_indexdef(i.indexrelid,3,true)='transactionconfirmationdata') THEN
        RAISE EXCEPTION 'Invalid idx_payment_batches_recovery_created; have an administrator inspect and replace it with the documented three-column B-tree';
    END IF;
    IF EXISTS(SELECT FROM pg_namespace WHERE nspname=audit_schema AND nspowner<>(SELECT oid FROM pg_roles WHERE rolname=admin_role)) THEN
        RAISE EXCEPTION 'Existing audit schema must be owned by this separate administrator';
    END IF;
    EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I AUTHORIZATION %I',audit_schema,admin_role);
    -- Never adopt an application-created lookalike into a SECURITY DEFINER write path.
    IF EXISTS(SELECT FROM pg_class c WHERE c.oid IN (
            to_regclass(format('%I.payout_manager_recovery_events',ownership_schema)),
            to_regclass(format('%I.payout_manager_recovery_events',audit_schema)))
        AND (c.relowner<>(SELECT oid FROM pg_roles WHERE rolname=admin_role) OR c.relkind<>'r' OR c.relrowsecurity)) THEN
        RAISE EXCEPTION 'Existing audit storage must be a plain table owned by this administrator without row security';
    END IF;
    -- Upgrade early recovery builds without deleting or recreating their audit history.
    IF to_regclass(format('%I.payout_manager_recovery_events',ownership_schema)) IS NOT NULL THEN
        IF to_regclass(format('%I.payout_manager_recovery_events',audit_schema)) IS NOT NULL THEN
            RAISE EXCEPTION 'Both legacy and protected audit tables exist; reconcile them before retrying';
        END IF;
        EXECUTE format('ALTER TABLE %I.payout_manager_recovery_events SET SCHEMA %I',ownership_schema,audit_schema);
    END IF;
    EXECUTE format('CREATE TABLE IF NOT EXISTS %I.payout_manager_recovery_events
(
    event_id UUID PRIMARY KEY,
    database_name TEXT NOT NULL,
    database_role TEXT NOT NULL,
    ownership_schema TEXT NOT NULL,
    ownership_relation BIGINT NOT NULL,
    owner_id UUID NOT NULL,
    generation BIGINT NOT NULL,
    owner_host TEXT,
    owner_process_id INT,
    acquired TIMESTAMPTZ,
    released TIMESTAMPTZ NOT NULL,
    releasing_host TEXT NOT NULL,
    releasing_process_id INT NOT NULL,
    incident_reference TEXT NOT NULL CHECK(length(btrim(incident_reference)) BETWEEN 1 AND 200),
    UNIQUE(ownership_relation, owner_id, generation)
);',audit_schema);
    EXECUTE format('ALTER TABLE %I.payout_manager_recovery_events OWNER TO %I',audit_schema,admin_role);
    EXECUTE format($ddl$CREATE OR REPLACE FUNCTION %I.guard_payout_recovery_events() RETURNS trigger
        LANGUAGE plpgsql SET search_path TO pg_catalog, pg_temp AS $body$
        BEGIN RAISE EXCEPTION 'Payout recovery audit events are append-only' USING ERRCODE='55000'; END $body$ $ddl$,audit_schema);
    EXECUTE format('DROP TRIGGER IF EXISTS trg_payout_recovery_events_append_only ON %I.payout_manager_recovery_events',audit_schema);
    EXECUTE format('CREATE TRIGGER trg_payout_recovery_events_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON %I.payout_manager_recovery_events FOR EACH STATEMENT EXECUTE FUNCTION %I.guard_payout_recovery_events()',audit_schema,audit_schema);
    -- Only typed caller-supplied evidence enters this function. It never executes SQL
    -- against application-owned relations under administrator privileges.
    EXECUTE format($ddl$CREATE OR REPLACE FUNCTION %I.record_payout_recovery(
        event uuid, owner_token uuid, owner_generation bigint, owner_machine text,
        owner_pid integer, owner_acquired timestamptz, owner_released timestamptz,
        releasing_machine text, releasing_pid integer, incident text)
        RETURNS TABLE(event_id uuid, released timestamptz)
        LANGUAGE plpgsql SECURITY DEFINER SET search_path TO pg_catalog, pg_temp AS $body$
        BEGIN
            -- Inspect only catalogs under administrator privileges. The caller must
            -- hold the same exclusive two-integer lock used by guarded recovery.
            IF NOT EXISTS(SELECT FROM pg_catalog.pg_locks
                WHERE locktype='advisory' AND pid=pg_catalog.pg_backend_pid()
                  AND database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
                  AND classid=19779 AND objid=5259609 AND objsubid=2
                  AND mode='ExclusiveLock' AND granted) THEN
                RAISE EXCEPTION 'Recovery audit append requires this backend to hold the exclusive payout advisory lock'
                    USING ERRCODE='55000';
            END IF;
            RETURN QUERY INSERT INTO %I.payout_manager_recovery_events AS recorded
                (event_id,database_name,database_role,ownership_schema,ownership_relation,owner_id,
                 generation,owner_host,owner_process_id,acquired,released,releasing_host,releasing_process_id,incident_reference)
            VALUES(event,current_database(),coalesce(nullif(current_setting('role'),'none'),session_user),%L,%s,
                owner_token,owner_generation,owner_machine,owner_pid,owner_acquired,owner_released,
                releasing_machine,releasing_pid,incident)
            RETURNING recorded.event_id,recorded.released;
        END
        $body$ $ddl$,audit_schema,audit_schema,ownership_schema,ownership);
    -- Remove inherited/default ACLs as well as direct grants, including column grants.
    FOR grantee IN SELECT DISTINCT pg_get_userbyid(a.grantee) FROM pg_namespace n,
        LATERAL aclexplode(n.nspacl) a WHERE n.nspname=audit_schema AND a.grantee<>0
        AND a.grantee<>(SELECT oid FROM pg_roles WHERE rolname=admin_role)
    LOOP EXECUTE format('REVOKE ALL ON SCHEMA %I FROM %I CASCADE',audit_schema,grantee); END LOOP;
    EXECUTE format('REVOKE ALL ON SCHEMA %I FROM PUBLIC',audit_schema);
    FOR grantee IN SELECT DISTINCT pg_get_userbyid(a.grantee) FROM pg_class c,
        LATERAL aclexplode(c.relacl) a WHERE c.oid=to_regclass(format('%I.payout_manager_recovery_events',audit_schema))
        AND a.grantee<>0 AND a.grantee<>(SELECT oid FROM pg_roles WHERE rolname=admin_role)
    LOOP EXECUTE format('REVOKE ALL ON %I.payout_manager_recovery_events FROM %I CASCADE',audit_schema,grantee); END LOOP;
    FOR entry IN SELECT at.attname,a.grantee FROM pg_attribute at,LATERAL aclexplode(at.attacl) a
        WHERE at.attrelid=to_regclass(format('%I.payout_manager_recovery_events',audit_schema)) AND at.attnum>0
        AND a.grantee<>(SELECT oid FROM pg_roles WHERE rolname=admin_role)
    LOOP EXECUTE format('REVOKE ALL (%I) ON %I.payout_manager_recovery_events FROM %s CASCADE',entry.attname,audit_schema,
        CASE WHEN entry.grantee=0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(entry.grantee)) END); END LOOP;
    EXECUTE format('REVOKE ALL ON %I.payout_manager_recovery_events FROM PUBLIC',audit_schema);
    FOR entry IN SELECT p.oid::regprocedure AS routine FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=audit_schema
    LOOP
        EXECUTE format('ALTER FUNCTION %s OWNER TO %I',entry.routine,admin_role);
        FOR grantee IN SELECT DISTINCT pg_get_userbyid(a.grantee) FROM pg_proc p,LATERAL aclexplode(p.proacl) a
            WHERE p.oid=entry.routine::oid AND a.grantee<>0 AND a.grantee<>(SELECT oid FROM pg_roles WHERE rolname=admin_role)
        LOOP EXECUTE format('REVOKE ALL ON FUNCTION %s FROM %I CASCADE',entry.routine,grantee); END LOOP;
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',entry.routine);
    END LOOP;
    -- Retire only administrator-owned historical endpoints with no live ownership
    -- table at their OID. Never read application-owned relations or change history/SELECT.
    -- A different live ownership schema must keep its independent append endpoint.
    FOR entry IN
        SELECT n.nspname, p.oid AS routine_oid, p.proowner
        FROM pg_catalog.pg_namespace n
        JOIN pg_catalog.pg_proc p ON p.pronamespace=n.oid
        JOIN pg_catalog.pg_class history ON history.relnamespace=n.oid
            AND history.relname='payout_manager_recovery_events' AND history.relkind='r'
        WHERE n.nspname ~ '^miningcore_audit_[0-9]+$'
          AND n.nspowner=(SELECT oid FROM pg_catalog.pg_roles WHERE rolname=admin_role)
          AND p.proowner=n.nspowner AND history.relowner=n.nspowner
          AND p.oid=to_regprocedure(format('%I.record_payout_recovery(uuid,uuid,bigint,text,integer,timestamptz,timestamptz,text,integer,text)',n.nspname))
          AND NOT EXISTS(SELECT FROM pg_catalog.pg_class live
              WHERE live.oid::text=substring(n.nspname::text FROM length('miningcore_audit_')+1)
                AND live.relname='payout_manager_ownership' AND live.relkind IN ('r','p'))
    LOOP
        signature := format('%I.record_payout_recovery(uuid,uuid,bigint,text,integer,timestamptz,timestamptz,text,integer,text)',entry.nspname);
        BEGIN
            FOR grantee IN SELECT DISTINCT pg_get_userbyid(a.grantee)
                FROM pg_catalog.pg_proc p,LATERAL aclexplode(p.proacl) a
                WHERE p.oid=entry.routine_oid AND a.grantee<>0 AND a.grantee<>entry.proowner
            LOOP EXECUTE format('REVOKE EXECUTE ON FUNCTION %s FROM %I CASCADE',signature,grantee); END LOOP;
            EXECUTE format('REVOKE EXECUTE ON FUNCTION %s FROM PUBLIC',signature);
            RAISE NOTICE 'Retired obsolete recovery append endpoint: %',signature;
        EXCEPTION WHEN undefined_function OR invalid_schema_name THEN
            -- A concurrent administrator may already have removed the historical endpoint.
            NULL;
        END;
    END LOOP;
    signature := format('%I.record_payout_recovery(uuid,uuid,bigint,text,integer,timestamptz,timestamptz,text,integer,text)',audit_schema);
    EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I',audit_schema,app_role);
    EXECUTE format('GRANT SELECT ON %I.payout_manager_recovery_events TO %I',audit_schema,app_role);
    EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO %I',signature,app_role);
END $migration$;
COMMIT;
