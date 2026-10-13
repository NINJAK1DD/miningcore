-- Read-only pre-upgrade audit for pooled shares and retained PPS receipts.
-- Never delete, repair or reprice financial rows automatically.
-- Requires both shares and pps_share_credits; missing schema is an incomplete
-- audit, not an empty clean result. Use a replica or a quiet maintenance period
-- on large databases: the repeatable-read snapshot lasts for every scan.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

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

-- This RAW comparison is meaningful only for identity AdjustShareDifficulty
-- contracts. Alephium, Ergo and Kaspa handlers require per-family/symbol scaling;
-- an empty raw report cannot clear those pools. See the operator guide.
-- Flags are not proof of abuse. Do not divide: the quotient can overflow.
SELECT poolid, count(*) AS raw_oversized_rows, min(created) AS first_created,
       max(created) AS last_created
FROM shares
WHERE difficulty > 0 AND difficulty < 'Infinity'::double precision
  AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
  AND difficulty > networkdifficulty
GROUP BY poolid
ORDER BY poolid;

-- PPS receipts can outlive their statistical share rows. Audit them independently.
-- Numeric NaN can pass positivity constraints, just like floating-point NaN.
SELECT poolid, count(*) AS invalid_pps_rows, min(created) AS first_created,
       max(created) AS last_created
FROM pps_share_credits
WHERE difficulty IS NULL OR networkdifficulty IS NULL
   OR difficulty <= 0 OR networkdifficulty <= 0
   OR difficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
   OR networkdifficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
   OR calculatedamount IS NULL OR calculatedamount <= 0
   OR calculatedamount IN ('NaN'::numeric, 'Infinity'::numeric, '-Infinity'::numeric)
   OR creditedamount IS NULL OR creditedamount < 0
   OR creditedamount IN ('NaN'::numeric, 'Infinity'::numeric, '-Infinity'::numeric)
GROUP BY poolid
ORDER BY poolid;

SELECT poolid, accountingid, address, created, difficulty, networkdifficulty,
       calculatedamount, creditedamount
FROM pps_share_credits
WHERE difficulty IS NULL OR networkdifficulty IS NULL
   OR difficulty <= 0 OR networkdifficulty <= 0
   OR difficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
   OR networkdifficulty IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)
   OR calculatedamount IS NULL OR calculatedamount <= 0
   OR calculatedamount IN ('NaN'::numeric, 'Infinity'::numeric, '-Infinity'::numeric)
   OR creditedamount IS NULL OR creditedamount < 0
   OR creditedamount IN ('NaN'::numeric, 'Infinity'::numeric, '-Infinity'::numeric)
ORDER BY poolid, created, accountingid
LIMIT 100;

-- Current integrated Bitcoin-family PPS uses common difficulty units. Imported
-- or custom PPS contracts must verify their unit scale before using raw flags.
-- These are retained receipt totals, NOT the current unpaid balance or a proven
-- overpayment. Sum only valid finite amounts; invalid receipts are above.
SELECT poolid, count(*) AS raw_oversized_credits,
       sum(calculatedamount) AS retained_calculated_liability,
       sum(creditedamount) AS retained_credited_amount,
       min(created) AS first_created, max(created) AS last_created
FROM pps_share_credits
WHERE difficulty > 0 AND difficulty < 'Infinity'::double precision
  AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
  AND calculatedamount > 0 AND calculatedamount < 'Infinity'::numeric
  AND creditedamount >= 0 AND creditedamount < 'Infinity'::numeric
  AND difficulty > networkdifficulty
GROUP BY poolid
ORDER BY poolid;

-- Retention also prunes PPS balance_changes. An empty retained ledger cannot
-- establish historical safety; reconcile archived receipts/changes/backups.
COMMIT;
