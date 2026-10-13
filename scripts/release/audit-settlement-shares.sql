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
-- Constraint-violating/NULL checks also cover imports and hand-built schemas.
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
-- Totals cover ONLY flagged oversized receipts, not all retained PPS receipts.
-- The linear excess estimate is a screening amount, not proven overpayment or
-- the current unpaid balance. Numeric arithmetic avoids binary64 underflow and
-- overflow; (D - N) / D avoids cancellation from subtracting a ratio near one.
-- Round the aggregate estimate to the calculated receipt's 24-decimal precision.
-- Sum only valid finite amounts; invalid receipts are above.
SELECT poolid, count(*) AS raw_oversized_credits,
       sum(calculatedamount) AS oversized_calculated_amount,
       sum(creditedamount) AS oversized_credited_amount,
       round(sum(calculatedamount * ((difficulty::numeric - networkdifficulty::numeric)
           / difficulty::numeric)), 24) AS estimated_oversized_excess,
       min(created) AS first_created, max(created) AS last_created
FROM pps_share_credits
WHERE difficulty > 0 AND difficulty < 'Infinity'::double precision
  AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
  AND calculatedamount > 0 AND calculatedamount < 'Infinity'::numeric
  AND creditedamount >= 0 AND creditedamount < 'Infinity'::numeric
  AND difficulty > networkdifficulty
GROUP BY poolid
ORDER BY poolid;

-- Merged auxiliary-only candidates can inflate a parent row even when D <= its
-- parent network difficulty. Parent=2, Auxiliary=3 in ShareAccountingRole.
-- EXISTS counts each parent once, even if an imported event has multiple peers.
-- These raw comparisons require common difficulty units on both projections.
SELECT p.poolid, count(*) AS paired_parent_rows,
       min(p.created) AS first_created, max(p.created) AS last_created
FROM shares p
WHERE p.accountingid IS NOT NULL AND p.accountingrole = 2
  AND p.difficulty > 0 AND p.difficulty < 'Infinity'::double precision
  AND p.networkdifficulty > 0 AND p.networkdifficulty < 'Infinity'::double precision
  AND p.difficulty <= p.networkdifficulty
  AND EXISTS (
      SELECT 1 FROM shares a
      WHERE a.accountingid = p.accountingid AND a.poolid <> p.poolid
        AND a.accountingrole = 3
        AND a.difficulty > 0 AND a.difficulty < 'Infinity'::double precision
        AND a.networkdifficulty > 0 AND a.networkdifficulty < 'Infinity'::double precision
        AND a.difficulty > a.networkdifficulty)
GROUP BY p.poolid
ORDER BY p.poolid;

SELECT p.poolid, p.accountingid, p.miner, p.created, p.blockheight,
       p.difficulty, p.networkdifficulty
FROM shares p
WHERE p.accountingid IS NOT NULL AND p.accountingrole = 2
  AND p.difficulty > 0 AND p.difficulty < 'Infinity'::double precision
  AND p.networkdifficulty > 0 AND p.networkdifficulty < 'Infinity'::double precision
  AND p.difficulty <= p.networkdifficulty
  AND EXISTS (
      SELECT 1 FROM shares a
      WHERE a.accountingid = p.accountingid AND a.poolid <> p.poolid
        AND a.accountingrole = 3
        AND a.difficulty > 0 AND a.difficulty < 'Infinity'::double precision
        AND a.networkdifficulty > 0 AND a.networkdifficulty < 'Infinity'::double precision
        AND a.difficulty > a.networkdifficulty)
ORDER BY p.poolid, p.created, p.accountingid
LIMIT 100;

-- PPS has no accountingrole column. Flag each valid nonoversized receipt once
-- when another pool has a valid oversized receipt with the same accountingid.
-- Verify parent/auxiliary direction from deployed topology or archived roles.
-- No shares join: retained PPS evidence remains useful after statistical pruning.
WITH valid_receipts AS (
    SELECT * FROM pps_share_credits
    WHERE accountingid IS NOT NULL
      AND difficulty > 0 AND difficulty < 'Infinity'::double precision
      AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
      AND calculatedamount > 0 AND calculatedamount < 'Infinity'::numeric
      AND creditedamount >= 0 AND creditedamount < 'Infinity'::numeric)
SELECT p.poolid, count(*) AS paired_nonoversized_receipts,
       sum(p.calculatedamount) AS paired_calculated_amount,
       sum(p.creditedamount) AS paired_credited_amount,
       min(p.created) AS first_created, max(p.created) AS last_created
FROM valid_receipts p
WHERE p.difficulty <= p.networkdifficulty
  AND EXISTS (
      SELECT 1 FROM valid_receipts a
      WHERE a.accountingid = p.accountingid AND a.poolid <> p.poolid
        AND a.difficulty > a.networkdifficulty)
GROUP BY p.poolid
ORDER BY p.poolid;

WITH valid_receipts AS (
    SELECT * FROM pps_share_credits
    WHERE accountingid IS NOT NULL
      AND difficulty > 0 AND difficulty < 'Infinity'::double precision
      AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
      AND calculatedamount > 0 AND calculatedamount < 'Infinity'::numeric
      AND creditedamount >= 0 AND creditedamount < 'Infinity'::numeric)
SELECT p.poolid, p.accountingid, p.address, p.created, p.difficulty,
       p.networkdifficulty, p.calculatedamount, p.creditedamount
FROM valid_receipts p
WHERE p.difficulty <= p.networkdifficulty
  AND EXISTS (
      SELECT 1 FROM valid_receipts a
      WHERE a.accountingid = p.accountingid AND a.poolid <> p.poolid
        AND a.difficulty > a.networkdifficulty)
ORDER BY p.poolid, p.created, p.accountingid
LIMIT 100;

-- Retention also prunes PPS balance_changes. An empty retained ledger cannot
-- establish historical safety; reconcile archived receipts/changes/backups.
COMMIT;
