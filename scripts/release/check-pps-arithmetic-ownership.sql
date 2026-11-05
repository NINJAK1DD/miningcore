-- Read-only prerequisite for the standalone arithmetic migration, not a migration.
-- Run as the same administrator, with the same database/search_path as that script.
-- No automatic ownership repair or fallback to cumulative SQL is permitted.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
DO $ownership_preflight$
DECLARE
    application_owner oid;
    accounting_table regclass;
BEGIN
    SELECT relowner INTO STRICT application_owner FROM pg_class WHERE oid = 'shares'::regclass;
    IF (SELECT rolsuper FROM pg_roles WHERE oid = application_owner) THEN
        RAISE EXCEPTION 'shares is owned by a superuser; align it to the non-superuser application role first'
            USING ERRCODE = '55000';
    END IF;
    FOREACH accounting_table IN ARRAY ARRAY[
        'share_accounting_groups'::regclass,
        'share_accounting_prune_state'::regclass,
        'pps_share_credits'::regclass,
        'pps_credit_remainders'::regclass
    ] LOOP
        IF (SELECT relowner FROM pg_class WHERE oid = accounting_table) IS DISTINCT FROM application_owner THEN
            RAISE EXCEPTION 'Accounting ownership differs from shares; reconcile ownership before the standalone arithmetic migration'
                USING ERRCODE = '55000';
        END IF;
    END LOOP;
END
$ownership_preflight$;
COMMIT;
