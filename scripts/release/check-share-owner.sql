-- Read-only prerequisite for cumulative accounting installation, not a migration.
-- Accounting tables may not exist yet. Never infer a safe application role from
-- grants alone or change ownership automatically.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
DO $share_owner_preflight$
DECLARE application_owner oid;
BEGIN
    SELECT relowner INTO STRICT application_owner FROM pg_class WHERE oid = 'shares'::regclass;
    IF (SELECT rolsuper FROM pg_roles WHERE oid = application_owner) THEN
        RAISE EXCEPTION 'shares is owned by a superuser; align it to the non-superuser application role first'
            USING ERRCODE = '55000';
    END IF;
END
$share_owner_preflight$;
COMMIT;
