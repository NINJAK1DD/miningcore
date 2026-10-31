-- Read-only, explicitly invoked operator audit. Use the current verified tip
-- and the cursor from the scan log/metric; never use these counts as liquidity.
-- Copy recovery_depth from the running version's scan log. No silent default.
-- psql -X -v ON_ERROR_STOP=1 -v pool_id=POOL -v tip_height=HEIGHT -v cursor=ID -v recovery_depth=DEPTH -f FILE
\set ON_ERROR_STOP on
BEGIN READ ONLY;
SET LOCAL statement_timeout = '5s';
WITH context AS (
    SELECT :'pool_id'::text AS pool_id, :'tip_height'::bigint AS tip_height,
           :'cursor'::bigint AS cursor,
           :'recovery_depth'::bigint AS recovery_depth,
           greatest(0::bigint, :'tip_height'::bigint - :'recovery_depth'::bigint) AS minimum_height
)
SELECT count(*) AS eligible_orphans,
       count(*) FILTER (WHERE b.id > c.cursor) AS remaining_in_sweep,
       min(b.blockheight) FILTER (WHERE b.id > c.cursor) AS oldest_remaining_height,
       min(b.created) FILTER (WHERE b.id > c.cursor) AS oldest_remaining_created,
       count(*) FILTER (WHERE b.blockheight <= c.minimum_height + c.recovery_depth / 20) AS approaching_horizon
FROM blocks b CROSS JOIN context c
WHERE b.poolid = c.pool_id AND b.status = 'orphaned'
  AND coalesce(b.type, 'block') = 'block'
  AND b.blockheight >= c.minimum_height AND c.tip_height > 0 AND c.cursor >= 0 AND c.recovery_depth > 0;
ROLLBACK;
