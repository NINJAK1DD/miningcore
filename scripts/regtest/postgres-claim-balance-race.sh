#!/usr/bin/env bash
set -euo pipefail

: "${PGHOST:?set PGHOST}"
: "${PGPORT:?set PGPORT}"
: "${PGUSER:?set PGUSER}"
: "${PGDATABASE:?set PGDATABASE}"
: "${PGPASSWORD:?set PGPASSWORD}"

pool="${1:-regtest-claim-balance-race}"
claimant="claim-miner-a"
winner="direct-miner-b"

cleanup() {
    psql -v ON_ERROR_STOP=1 -v pool="$pool" -v claimant="$claimant" -v winner="$winner" <<'SQL' >/dev/null
DELETE FROM balance_changes WHERE poolid = :'pool';
DELETE FROM balances WHERE poolid = :'pool';
DELETE FROM blocks WHERE poolid = :'pool';
SQL
}
trap cleanup EXIT
cleanup

psql -v ON_ERROR_STOP=1 -v pool="$pool" -v claimant="$claimant" -v winner="$winner" <<'SQL'
INSERT INTO blocks(poolid, blockheight, networkdifficulty, status, type,
    transactionconfirmationdata, miner, hash, reward, created)
VALUES
    (:'pool', 1, 1, 'pending', 'auxpow-claim',
        'auxpow-claim:race-child:proof-a:0', :'claimant', 'race-child', 100, now()),
    (:'pool', 1, 1, 'pending', 'auxpow',
        'auxpow-block:race-child', :'winner', 'race-child', 100, now());

-- This mirrors the transition-first application transaction. Because the direct
-- final row is already visible, promotion updates zero rows and both credit CTEs
-- consume an empty transition result.
BEGIN;
WITH promoted AS (
    UPDATE blocks claim
    SET type = 'auxpow', status = 'confirmed',
        transactionconfirmationdata = 'race-coinbase'
    WHERE claim.poolid = :'pool'
      AND claim.hash = 'race-child'
      AND claim.type = 'auxpow-claim'
      AND NOT EXISTS (
          SELECT 1 FROM blocks finalized
          WHERE finalized.poolid = claim.poolid
            AND finalized.hash = claim.hash
            AND finalized.type = 'auxpow'
            AND finalized.id <> claim.id)
    RETURNING 1
), recorded AS (
    INSERT INTO balance_changes(poolid, address, amount, usage, created)
    SELECT :'pool', :'claimant', 100, 'claim reward', now()
    FROM promoted
    RETURNING 1
)
INSERT INTO balances(poolid, address, amount, created, updated)
SELECT :'pool', :'claimant', 100, now(), now()
FROM promoted
ON CONFLICT(poolid, address) DO UPDATE
SET amount = balances.amount + EXCLUDED.amount, updated = now();
COMMIT;

-- Process the actual final row and credit only its beneficiary.
BEGIN;
WITH transitioned AS (
    UPDATE blocks
    SET status = 'confirmed', transactionconfirmationdata = 'race-coinbase'
    WHERE poolid = :'pool' AND hash = 'race-child'
      AND type = 'auxpow' AND status = 'pending'
    RETURNING 1
), recorded AS (
    INSERT INTO balance_changes(poolid, address, amount, usage, created)
    SELECT :'pool', :'winner', 100, 'direct reward', now()
    FROM transitioned
    RETURNING 1
)
INSERT INTO balances(poolid, address, amount, created, updated)
SELECT :'pool', :'winner', 100, now(), now()
FROM transitioned
ON CONFLICT(poolid, address) DO UPDATE
SET amount = balances.amount + EXCLUDED.amount, updated = now();
COMMIT;
SQL

result="$(psql -At -v pool="$pool" -v claimant="$claimant" -v winner="$winner" <<'SQL'
SELECT
    (SELECT count(*) FROM balances WHERE poolid = :'pool' AND address = :'claimant') || ':' ||
    (SELECT count(*) FROM balance_changes WHERE poolid = :'pool' AND address = :'claimant') || ':' ||
    (SELECT count(*) FROM balances WHERE poolid = :'pool' AND address = :'winner' AND amount = 100) || ':' ||
    (SELECT count(*) FROM balance_changes WHERE poolid = :'pool' AND address = :'winner' AND amount = 100) || ':' ||
    (SELECT count(*) FROM blocks WHERE poolid = :'pool' AND hash = 'race-child' AND type = 'auxpow' AND status = 'confirmed');
SQL
)"

echo "claim-balances:claim-changes:winner-balances:winner-changes:payable = $result"
if [[ "$result" != "0:0:1:1:1" ]]; then
    echo "AuxPoW losing-claim balance race validation failed" >&2
    exit 1
fi
