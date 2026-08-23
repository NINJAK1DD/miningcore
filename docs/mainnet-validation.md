# Mainnet validation

This record preserves production evidence for release decisions. It is not an installation
procedure or a guarantee for other deployments. Append new, dated evidence instead of rewriting
historical observations. The chain data for a named public block is inherently discoverable; the
abbreviated identifiers below are publication hygiene, not a claim of on-chain non-disclosure.
Never publish credentials, complete copy-pasteable transaction identifiers, wallet addresses, peer
addresses, or private host and process identities with an audit.

## RC.8 Dogecoin merged-mining payout

One production Dogecoin auxiliary block completed the full financial cycle on
`v0.1.0-rc.8` (`047c07884e45c50aed01118b80ad42aca91c4bb4`) on 7–8 August 2026.

**Candidate and confirmation.** Pool `doge1-solo` found height `6321518`. PostgreSQL retained exactly
one confirmed `auxpow` row with reward `10005.341398600000 DOGE`, block hash
`ad6142a4...173c6` and coinbase transaction `a2c8b4f6...35f83`.

**Initial payout attempt.** After maturity, the wallet rejected scheduled `sendmany` with
`Insufficient funds` code `-6`. The reward was present, but the wallet had no separate confirmed
input for the transaction fee. Miningcore retained the balance and retried normally; no manual
payment or database edit was made.

**Accepted payout.** After the same wallet received a confirmed fee reserve, Miningcore submitted
transaction `6d5ab821...1a423c` once. The audited response showed two outputs totalling
`10005.3413 DOGE`, a `0.00408 DOGE` fee, 706 confirmations and no wallet conflicts.

**Accounting.** The miner was credited `9905.287984614 DOGE`. Miningcore persisted and reset
`9905.2879 DOGE`, leaving the expected miner-side precision residual of `0.000084614 DOGE`. The
complete wallet request was `0.0000986 DOGE` below the block reward because both outputs were
truncated to configured payout precision. Configured reward recipients are deliberately excluded
from public `payments` rows.

**Idempotency.** PostgreSQL contained one block row and one payment-batch row for the transaction.
The public miner payment appeared once. A scan of 75,779 service-journal lines after batch
persistence found no later matching payout failure, while scheduled cycles reported no balance
over the configured minimum.

**Restart and ownership.** A controlled daemon and Miningcore restart loaded the payout wallets,
reacquired payout ownership as generation 28, and recorded the active Miningcore PID as owner.
Subsequent block reconciliation and payment cycles remained healthy.

This closes the live Dogecoin merged-mining and Bitcoin-family payout-cycle gate for RC.8: candidate
attribution, maturity, balance credit, conclusive wallet rejection, scheduled retry, durable
payment persistence, on-chain confirmation, precision retention and clean restart were all observed.
It does not constitute a live Bitcoin or Litecoin block-payout test, cover an uncertain wallet
response, or validate a deployment with different daemons, wallets, fees, topology or configuration.
Those paths retain their daemon-backed, fault-injection and PostgreSQL evidence in the
[regtest validation record](merged-mining-regtest-validation.md).

### Sanitized audit commands

The production audit used read-only wallet, PostgreSQL and service-journal checks. Replace every
placeholder when repeating it; do not publish RPC credentials, wallet addresses, complete
transaction identifiers, peer data or host data with the results.

Supply the same configuration or data directory, network, RPC endpoint, authentication, proxy and
wallet-selection arguments used by the production daemon. The abbreviated commands below assume
`dogecoin-cli` already resolves that exact instance; do not rely on its defaults when multiple nodes,
networks or data directories exist.

```console
TXID='REPLACE_WITH_TRANSACTION_ID'
dogecoin-cli REPLACE_WITH_PRODUCTION_RPC_ARGUMENTS gettransaction "$TXID" true
dogecoin-cli REPLACE_WITH_PRODUCTION_RPC_ARGUMENTS getwalletinfo
```

Connect as Miningcore's least-privileged database role, following the
[database role setup](database.md#new-installation), rather than relying on the
PostgreSQL operating-system administrator:

```console
psql -X -h REPLACE_WITH_DATABASE_HOST \
  -U REPLACE_WITH_MININGCORE_ROLE \
  -d REPLACE_WITH_DATABASE -P pager=off \
  -v pool_id='doge1-solo' \
  -v block_height='6321518' \
  -v txid='REPLACE_WITH_TRANSACTION_ID' \
  -v miner_address='REPLACE_WITH_MINER_ADDRESS' <<'SQL'
SELECT poolid, blockheight, status, type, reward,
       transactionconfirmationdata, hash, created
FROM REPLACE_WITH_SCHEMA.blocks
WHERE poolid = :'pool_id'
  AND blockheight = :'block_height'::bigint
ORDER BY id;

SELECT batch.poolid,
       batch.transactionconfirmationdata,
       batch.created,
       COUNT(payment.id) AS public_recipient_count,
       COALESCE(SUM(payment.amount), 0) AS public_payment_total
FROM REPLACE_WITH_SCHEMA.payment_batches AS batch
LEFT JOIN REPLACE_WITH_SCHEMA.payments AS payment
  ON payment.poolid = batch.poolid
 AND payment.transactionconfirmationdata = batch.transactionconfirmationdata
WHERE batch.poolid = :'pool_id'
  AND batch.transactionconfirmationdata = :'txid'
GROUP BY batch.poolid, batch.transactionconfirmationdata, batch.created;

SELECT poolid, amount, updated
FROM REPLACE_WITH_SCHEMA.balances
WHERE poolid = :'pool_id'
  AND address = :'miner_address';

SELECT generation,
       owner_host,
       owner_process_id,
       acquired,
       released
FROM REPLACE_WITH_SCHEMA.payout_manager_ownership
WHERE id = 1;
SQL
```

Compare `owner_host` and `owner_process_id` privately with the running service before recording the
ownership claim. Redact them from published evidence. This proves process identity only; use the
full [payout ownership recovery procedure](database.md#recover-payout-manager-ownership-safely) when
an old owner, advisory lock or uncertain transaction must be reconciled.

```console
hostname
systemctl show miningcore -p MainPID -p ActiveState -p SubState --no-pager
```

Use the persisted batch timestamp to bound the journal inspection, converting it to a systemd
timestamp first rather than passing PostgreSQL's offset form directly. The patterns below match the
current Bitcoin-family payout logs:

```console
BATCH_TIME='REPLACE_WITH_BATCH_CREATED_TIMESTAMP'
SINCE="$(date --date="$BATCH_TIME" '+%Y-%m-%d %H:%M:%S')"
TXID='REPLACE_WITH_TRANSACTION_ID'
PATTERN="doge1-solo|$TXID|Preparing wallet request:|Payment transaction id:"
PATTERN="$PATTERN|Resetting balance of|sendmany returned error:"
PATTERN="$PATTERN|No balances over configured minimum payout"
sudo journalctl -u miningcore --since "$SINCE" -o cat --no-pager |
  grep -iE "$PATTERN"
```

## Production validation status

Do not enable mainnet funds solely because the passed items below are green.

1. **Physical relay route — passed for the current lab.** Real traffic, interruption and reconnect
   passed between two physical hosts on a routed LAN, including sender-side firewall fault
   injection, PostgreSQL persistence and exact merged-block attribution. If the production hosts,
   firewall or route differ, repeat `bash scripts/regtest/validate-physical-relay.sh` on that final
   path. Ordinary ZeroMQ shares remain intentionally unacknowledged and are not replayed after an
   outage; this is an accepted deployment characteristic, not durable-queue validation.
2. **Payout-manager ownership — tests passed; operating rule remains.** The fail-closed backend
   termination, controlled recovery, block-credit serialization and payment-batch idempotency tests
   passed against PostgreSQL 17. Automatic/hot-standby failover remains intentionally unsupported.
   After every unclean stop, confirm the old process has fully terminated and reconcile wallet
   history before explicitly clearing its durable ownership row. A TCP forwarding layer may keep
   the dead process's PostgreSQL backend alive temporarily; if its verified backend still holds the
   advisory lock, terminate that backend as part of the same controlled recovery.
3. **Financial cycles — Dogecoin passed; Bitcoin and Litecoin pending.** The evidence above validates
   the live auxiliary-chain cycle and the shared Bitcoin-family `sendmany` persistence path. Bitcoin
   and Litecoin mainnet block payouts remain unobserved; retain the regtest evidence and repeat the
   read-only audit after their first production block rather than attempting to manufacture one.
