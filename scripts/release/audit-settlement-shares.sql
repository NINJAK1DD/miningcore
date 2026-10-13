-- Read-only pre-upgrade audit for all families using PROP, PPLNS or PPLNSBF.
-- Never delete, repair or reprice financial rows automatically.
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

-- Positive finite oversized credits also need historical economic review.
-- They are not proof of abuse. Do not divide: the quotient itself can overflow.
SELECT poolid, count(*) AS oversized_rows, min(created) AS first_created,
       max(created) AS last_created
FROM shares
WHERE difficulty > 0 AND difficulty < 'Infinity'::double precision
  AND networkdifficulty > 0 AND networkdifficulty < 'Infinity'::double precision
  AND difficulty > networkdifficulty
GROUP BY poolid
ORDER BY poolid;

COMMIT;
