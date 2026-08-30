# Pay Per Share (PPS)

Miningcore supports transactional PPS accounting for audited Bitcoin-family pools. A valid share
creates a miner liability when its accounting envelope commits to PostgreSQL; finding, losing or
orphaning a later block does not change that credit.

| Task | Start here |
| --- | --- |
| Decide whether PPS is suitable | [Economic and support boundary](#economic-and-support-boundary) |
| Prepare an existing database | [Database prerequisites](#database-prerequisites) |
| Configure a direct pool | [Direct configuration](#direct-configuration) |
| Use PPS with LTC/DOGE | [Merged-mining PPS](#merged-mining-pps) |
| Commission and monitor it | [Pre-production checklist](#pre-production-checklist) |
| Respond to an accounting incident | [Recovery and rollback](#recovery-and-rollback) |

For symptom-first diagnosis, use [Troubleshooting](troubleshooting.md). For table sizing, archival
and SQL inspection, use the [database runbook](database.md#share-accounting-retention-and-sizing).

## Economic and support boundary

PPS transfers block variance to the operator. Miningcore credits valid work before the pool knows
whether it will find enough mature blocks to cover that liability. The operator therefore assumes
network-difficulty, block-luck, reorganisation, daemon, wallet, liquidity and insolvency risk.

For assigned share difficulty `d`, network difficulty `D`, spendable template reward `B`, and the
positive reward-recipient fraction `f`, Miningcore calculates:

```text
(1 - f) × d ÷ D × B
```

The assigned difficulty is used; an unusually lucky share does not receive a larger PPS credit.
The exact liability is retained at 24 decimal places, the payable balance is rounded down to 12
decimal places, and the sub-unit remainder is carried per pool and miner.

Current support is deliberately narrow:

| Deployment | PPS status |
| --- | --- |
| Direct audited `Bitcoin`-family pool | Supported |
| Integrated LTC/DOGE | Supported independently on parent and auxiliary |
| Non-Bitcoin-family pool | Rejected before listeners open |
| `PPBS` or `PPLNSBF` | Not implemented by this contract |

Do not enable PPS merely because the configuration accepts it.
Maintain a separately controlled reserve. Size it for an extended unlucky period, expected
withdrawals, payout fees and an emergency shutdown. Reward-recipient percentages reduce the miner
basis but do not replace a solvency policy. Never use future block income as the only available
liquidity for already credited balances.

## Database prerequisites

A new database created from the current `createdb.sql` already contains the required contracts. To
upgrade an existing database, stop every Miningcore writer, relay recorder, recovery importer and
payout manager, take and verify a backup, then apply all three migrations in order:

```console
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f /opt/miningcore/migrations/add_auxpow_block_idempotency.sql
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f /opt/miningcore/migrations/add_payout_manager_ownership.sql
sudo -u postgres psql -v ON_ERROR_STOP=1 -d miningcore \
  -f /opt/miningcore/migrations/add_share_accounting.sql
```

These migrations provide synchronous accepted-block idempotency, one durable payout owner, and the
atomic receipt/credit/remainder ledger. Startup checks them before accepting PPS work. Do not create
lookalike tables or indexes to bypass preflight; reapply the supplied migration while Miningcore is
stopped if the diagnostic names a repairable contract.

The commands use the stable symlink created by the prebuilt installation path. A source-build
operator should substitute the checkout's
`src/Miningcore/Persistence/Postgres/Scripts/` directory while preserving the same file order.

Follow the complete [database upgrade procedure](database.md#upgrade-an-existing-database),
including its owner/privilege query and rollback warning. The accounting migration is additive, but
its liabilities and replay evidence are not reconstructible from blocks. Dropping its tables is not
a rollback.

## Direct configuration

Start from the reviewed direct example for the intended coin. The Bitcoin example remains `SOLO`
by default because changing it to PPS creates an immediate financial liability. After completing
the database and reserve work above, configure the cluster payout scheduler and the pool:

```json
{
  "paymentProcessing": {
    "enabled": true,
    "interval": 600,
    "shareAccountingPruneBatchSize": 50000,
    "shareAccountingRetentionDays": 30,
    "coinbaseString": "Miningcore"
  },
  "pools": [
    {
      "id": "bitcoin-pps",
      "paymentProcessing": {
        "enabled": true,
        "minimumPayment": 0.001,
        "payoutScheme": "PPS",
        "ppsShareRetentionDays": 7
      }
    }
  ]
}
```

The snippet shows only the fields changed or reviewed for PPS; it is not a complete pool
configuration. Keep the daemon, wallet, Stratum, banning and recovery settings from the chosen
example. `minimumPayment` is denominated in the pool coin. Do not rename a pool after it has ledger
history, because pool ID is part of the accounting and idempotency identity.

`ppsShareRetentionDays` controls statistical share rows only. It does not erase balances,
precision remainders or liabilities when a block settles. `shareAccountingRetentionDays` is the
maximum accepted relay/recovery replay age; keep the same value on every participating node and
choose a period longer than the longest supported outage or incident-response window.

## Merged-mining PPS

In integrated Litecoin/Dogecoin merged mining, the parent and auxiliary pools independently choose
`SOLO`, `PPS`, `PROP` or `PPLNS`. One proof can therefore create zero, one or two PPS liabilities.
When Dogecoin is not `SOLO`, set `requireAuxAddress: true` so every auxiliary projection belongs to
a daemon-validated miner address.

Use the reviewed [LTC/DOGE example](../examples/litecoin_dogecoin_merged_mining_pool.json) and the
complete [merged-mining guide](merged-mining-litecoin-dogecoin.md). Miners still connect only to the
Litecoin Stratum endpoint and supply the Dogecoin beneficiary through the configured password
parameter.

Upgrade and migrate relay receivers/recorders before senders. Every sender that accepts PPS work
also needs PostgreSQL, the candidate/accounting migrations and protected emergency-journal storage,
because daemon-accepted direct candidates are persisted synchronously on the submitting node. Do
not enable pooled merged mining or PPS during a mixed accounting-wire-version window.

## Pre-production checklist

Before admitting production miners:

1. Restore the pre-upgrade PostgreSQL backup in an isolated test database and prove it is usable.
2. Apply the migrations while all writers and payout managers are stopped; verify table ownership.
3. Confirm the pool wallet has a separately monitored PPS reserve and a confirmed transaction-fee
   reserve appropriate to `minersPayTxFees` and the coin's wallet behavior.
4. Start the exact production configuration in staging and require all schema, template, daemon,
   wallet and listener preflights to pass without bypasses.
5. Submit a bounded set of representative shares and confirm one credit per accounting identity,
   the expected balance increase and a remainder below `0.000000000001`.
6. Verify an exact replay does not credit twice, then perform a clean stop/start and confirm
   balances and payout ownership are unchanged.
7. Alert on database/journal failure, accounting quarantine, queue pressure, replay rejection,
   unsupported relay wire format, retention backlog and wallet reserve.

Use the exact ledger queries in [Routine inspection](database.md#routine-inspection). Prometheus
PPS totals are operational floating-point telemetry; PostgreSQL `pps_share_credits`,
`pps_credit_remainders`, `balance_changes` and `balances` are the financial record. Metric names and
labels are documented in [API and monitoring](api.md#metrics-and-administration).

## Recovery and rollback

- A stale or orphaned block does not reverse a committed PPS liability. A confirmed block does not
  add a second PPS credit.
- Do not edit balances, delete accounting groups or discard remainders to repair an incident.
- Preserve the service journal, PostgreSQL state, normal recovery journal, quarantine evidence and
  recovery-state directory before restarting.
- Import only the normal manifested recovery journal through the documented `-rs` procedure. A
  quarantine file is evidence for manual reconciliation and must never be passed directly to `-rs`.
- Treat an uncertain wallet submission as potentially paid until wallet and database evidence prove
  otherwise.

Follow [recovery-journal import](database.md#inspect-and-import-a-recovery-journal),
[fatal-state reconciliation](database.md#reconcile-fatal-share-recovery-state), or
[payout reconciliation](database.md#reconcile-a-bitcoin-family-payout) according to the first
failure. If application rollback is required after PPS has accepted work, keep miners offline and
restore the verified pre-migration database into an isolated replacement. Reconcile every balance
and payment created after that backup before directing miners or wallets to the older application.
