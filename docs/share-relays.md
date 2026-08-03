# Share relays

Share relay separates public Stratum servers from a central recorder. It is an advanced deployment
model: most pools should run Miningcore and PostgreSQL on one protected Linux host unless load or
network placement requires separate roles.

## Roles

- A **relay sender** accepts miners and publishes validated ordinary shares through `shareRelay`.
  It does not run the local ordinary-share recorder.
- A **receiver/recorder** subscribes through `shareRelays`, writes ordinary shares to PostgreSQL and
  is normally the only payout/reconciliation owner.
- A direct node can also subscribe to senders. When `shareRelays` is configured, internal Stratum
  defaults to disabled; set `enableInternalStratum: true` on a pool only when that receiver should
  also accept miners itself.

Only one process may own payouts for a database/pool set. A non-merged relay sender can be
database-free when payment processing is disabled there. A Litecoin–Dogecoin merged-mining sender
must have PostgreSQL because financially significant parent and auxiliary candidates are persisted
synchronously on the submitting node; see the [merged-mining guide](merged-mining-litecoin-dogecoin.md).

## Durability boundary

The ordinary-share relay is ZeroMQ PUB/SUB, not an acknowledged durable queue:

- a sender's positive Stratum response proves admission to its local in-memory relay queue;
- it does not prove that the receiver or PostgreSQL stored the share;
- shares published while the receiver or route is unavailable are not replayed; and
- sender restart or sudden host loss can discard queued ordinary shares.

The receiver's PostgreSQL queue and recovery journal protect shares only after the receiver has
received them. Monitor the complete route and accept this loss boundary before using relays. Merged
block candidates use separate synchronous PostgreSQL/recovery-journal persistence and are not
delegated to ordinary PUB/SUB delivery.

## Recommended bind mode

In the usual topology, each sender binds one protected relay endpoint and the central recorder
connects to it. Generate a different long random secret for each sender/receiver relationship and
keep the port private to the participating hosts.

Sender:

```json
"shareRelay": {
  "publishUrl": "tcp://192.0.2.20:5555",
  "connect": false,
  "sharedEncryptionKey": "CHANGE_ME_TO_A_LONG_RANDOM_SECRET"
}
```

Receiver/recorder:

```json
"shareRelays": [
  {
    "url": "tcp://192.0.2.20:5555",
    "sharedEncryptionKey": "CHANGE_ME_TO_A_LONG_RANDOM_SECRET"
  }
]
```

`192.0.2.20` is a documentation-only address; replace it with the sender's private address or DNS
name. The endpoint must be reachable from the receiver. Bind to an interface appropriate for the
host and restrict the port with host and network firewalls. Never commit the shared secret or place a
real secret in an issue, log excerpt or example configuration.

The first publisher/subscriber connection has the normal ZeroMQ slow-joiner window. Start the
receiver before admitting miners to a new sender and confirm receiver logs and PostgreSQL inserts
before moving traffic.

## Connect mode

`"connect": true` makes the sender connect instead of bind. Use it only with an operator-managed
ZeroMQ proxy/broker that binds the matching endpoint. Miningcore receivers also connect, so simply
setting connect mode on a sender without a broker leaves no side listening.

Miningcore intentionally rejects `sharedEncryptionKey` on a connect-mode sender because its built-in
Curve setup assumes that the sender is the Curve server. If a broker topology is required, protect
and authenticate that transport outside this built-in bind-mode arrangement and validate it in the
actual deployment lab.

## Receiver and database configuration

The receiver requires PostgreSQL and the same enabled pool IDs and coin definitions needed to
interpret incoming shares. Configure cluster and pool payment processing only on the one intended
payout owner. Use an absolute `shareRecoveryFile` path on separately monitored or reserved storage
where possible.

With a partitioned `shares` table, create an exact partition for every enabled receiver pool before
startup. Database setup, migrations and recovery are covered by the [database guide](database.md).

## Validation checklist

1. Start PostgreSQL, the receiver, and then each sender.
2. Confirm the sender reports its bound/connected endpoint and the receiver reports each monitored
   source without exposing shared secrets.
3. Submit a low-risk test share through every sender and verify it appears once in PostgreSQL with
   the expected pool, miner, worker and source timestamp.
4. Confirm the central receiver is the only payout owner.
5. Interrupt the relay port, verify monitoring detects the outage, restore it and confirm new shares
   resume without duplicates.
6. Record explicitly that ordinary shares sent during the interruption were not replayed.
7. If merged mining is enabled, separately verify synchronous parent/auxiliary block persistence and
   the required database migrations.

The repository includes `scripts/regtest/validate-physical-relay.sh` for a final physical-path check.
Its arguments and PostgreSQL environment variables are described in the
[merged-mining validation plan](merged-mining-litecoin-dogecoin.md#pre-production-validation).
