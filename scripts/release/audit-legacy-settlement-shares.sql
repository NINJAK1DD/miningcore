-- Read-only, SHARES-ONLY screening for positively established legacy deployments
-- that have NEVER used PPS or pooled merged accounting in this database.
-- Missing accounting schema is NOT evidence that this prerequisite is satisfied.
-- Never select this script as an automatic fallback when the full audit fails.
-- It cannot audit PPS receipts, paired events, balances, liabilities or payouts.
-- Never delete, repair or reprice rows automatically. Retention limits coverage.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

SELECT 'legacy-shares-only' AS audit_scope,
       'Retained statistical shares only; no PPS receipt, merged-event or payout reconciliation. Missing/pruned financial evidence is not cleared.' AS coverage_limit;

-- PostgreSQL considers NaN > 0 true, so check special values explicitly.
SELECT poolid, count(*) AS invalid_rows, min(created) AS first_created,
       max(created) AS last_created
FROM shares
WHERE difficulty IS NULL OR networkdifficulty IS NULL
   OR difficulty <= 0 OR networkdifficulty <= 0
   OR difficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
   OR networkdifficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
GROUP BY poolid
ORDER BY poolid;

-- Bounded identifying sample; reconcile the complete population separately.
SELECT poolid, miner, created, blockheight, difficulty, networkdifficulty
FROM shares
WHERE difficulty IS NULL OR networkdifficulty IS NULL
   OR difficulty <= 0 OR networkdifficulty <= 0
   OR difficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
   OR networkdifficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
ORDER BY poolid, created, miner
LIMIT 100;

-- RAW comparison only: identity AdjustShareDifficulty / common difficulty units
-- are required. Nonidentity handlers need the documented family/symbol scaling;
-- an empty report cannot clear them. Flags are not proof of abuse or overpayment.
-- Do not divide: a quotient can overflow. NaN/infinities are excluded explicitly
-- by the finite interval comparisons and reported in the invalid summary above.
SELECT poolid, count(*) AS raw_oversized_rows, min(created) AS first_created,
       max(created) AS last_created
FROM shares
WHERE difficulty > 0 AND difficulty < 'Infinity'::double precision
  AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
  AND difficulty > networkdifficulty
GROUP BY poolid
ORDER BY poolid;

COMMIT;
