-- Metadata-only closure of an independently audited historical allocation case.
-- Read docs/bitcoin-blake2b.md before use. Stop all financial writers first.
-- This script neither credits balances nor marks the block Confirmed.
-- Inputs are psql variables, quoted as SQL literals; default execution rolls back.
\set ON_ERROR_STOP on
\if :{?apply}
\else
\set apply false
\endif

BEGIN;
CREATE TEMP TABLE blake2b_resolution_context ON COMMIT DROP AS
SELECT :'pool_id'::text AS pool_id, :'block_id'::bigint AS block_id,
       :'block_height'::bigint AS block_height, :'block_hash'::text AS block_hash,
       :'coinbase_txid'::text AS coinbase_txid, :'case_id'::uuid AS case_id,
       :'credit_change_ids'::jsonb AS credit_change_ids,
       :'approved_additional_credit'::numeric AS approved_additional_credit;

DO $resolution$
DECLARE
    ctx blake2b_resolution_context%ROWTYPE;
    candidate blocks%ROWTYPE;
    receipt_ids bigint[];
    memo text;
    prefix text;
    resolution_tag text;
    receipt_count bigint;
    receipt_total numeric;
BEGIN
    SELECT * INTO STRICT ctx FROM blake2b_resolution_context;
    IF ctx.pool_id = '' OR ctx.block_id <= 0 OR ctx.block_height < 0
       OR ctx.block_hash !~ '^[[:xdigit:]]{64}$'
       OR ctx.coinbase_txid !~ '^[[:xdigit:]]{64}$'
       OR ctx.approved_additional_credit < 0
       OR ctx.approved_additional_credit >= 10000000000000000::numeric
       OR scale(ctx.approved_additional_credit) > 12
       OR jsonb_typeof(ctx.credit_change_ids) <> 'array'
    THEN RAISE EXCEPTION 'Invalid historical allocation resolution inputs'; END IF;

    IF EXISTS (SELECT 1 FROM jsonb_array_elements(ctx.credit_change_ids) item
               WHERE jsonb_typeof(item) <> 'number' OR item::text !~ '^[1-9][0-9]*$')
    THEN RAISE EXCEPTION 'Credit change IDs must be positive integers'; END IF;
    SELECT array_agg(item::text::bigint ORDER BY item::text::bigint)
    INTO receipt_ids FROM jsonb_array_elements(ctx.credit_change_ids) item;
    IF coalesce(cardinality(receipt_ids), 0) = 0 OR
       cardinality(receipt_ids) <> (SELECT count(DISTINCT id) FROM unnest(receipt_ids) id)
    THEN RAISE EXCEPTION 'Credit change IDs must be nonempty and unique'; END IF;

    SELECT * INTO STRICT candidate FROM blocks WHERE id = ctx.block_id FOR UPDATE;
    IF candidate.poolid IS DISTINCT FROM ctx.pool_id
       OR candidate.blockheight IS DISTINCT FROM ctx.block_height
       OR lower(candidate.hash) IS DISTINCT FROM lower(ctx.block_hash)
       OR lower(candidate.transactionconfirmationdata) IS DISTINCT FROM lower(ctx.coinbase_txid)
       OR candidate.status IS DISTINCT FROM 'quarantined'
       OR (candidate.type IS NOT NULL AND candidate.type <> 'block')
       OR coalesce(to_jsonb(candidate)->>'settlementmode', '') = 'coinbase-direct'
    THEN RAISE EXCEPTION 'Quarantined custodial block identity does not match'; END IF;

    memo := format('Audited Bitcoin BLAKE2b recovery block %s case %s', ctx.block_id, ctx.case_id);
    prefix := format('bitcoin-blake2b:allocation-resolved:block=%s:case=', ctx.block_id);
    resolution_tag := prefix || ctx.case_id::text;
    IF EXISTS (SELECT 1 FROM balance_changes b, unnest(b.tags) tag
               WHERE b.poolid = ctx.pool_id AND left(tag, length(prefix)) = prefix
                 AND (tag <> resolution_tag OR b.usage IS DISTINCT FROM memo
                      OR NOT b.id = ANY(receipt_ids)))
    THEN RAISE EXCEPTION 'Block already has a conflicting resolution receipt'; END IF;

    PERFORM id FROM balance_changes
    WHERE poolid = ctx.pool_id AND id = ANY(receipt_ids) ORDER BY id FOR UPDATE;
    SELECT count(*), sum(b.amount) INTO receipt_count, receipt_total
    FROM balance_changes b
    WHERE b.poolid = ctx.pool_id AND b.id = ANY(receipt_ids) AND b.usage = memo
      AND b.amount >= 0 AND EXISTS (SELECT 1 FROM balances a
          WHERE a.poolid = b.poolid AND a.address = b.address);
    IF receipt_count <> cardinality(receipt_ids)
       OR receipt_count <> (SELECT count(*) FROM balance_changes WHERE poolid = ctx.pool_id AND usage = memo)
       OR receipt_total IS DISTINCT FROM ctx.approved_additional_credit
    THEN RAISE EXCEPTION 'Audited recovery receipts do not match the approved case and total'; END IF;

    UPDATE balance_changes SET tags = array_append(coalesce(tags, ARRAY[]::text[]), resolution_tag)
    WHERE poolid = ctx.pool_id AND id = ANY(receipt_ids)
      AND NOT resolution_tag = ANY(coalesce(tags, ARRAY[]::text[]));
END
$resolution$;

SELECT b.id, b.poolid, b.address, b.amount, b.usage, b.tags
FROM balance_changes b, blake2b_resolution_context ctx
WHERE b.poolid = ctx.pool_id AND b.id IN
    (SELECT item::text::bigint FROM jsonb_array_elements(ctx.credit_change_ids) item)
ORDER BY b.id;

\if :apply
COMMIT;
\else
ROLLBACK;
\endif
