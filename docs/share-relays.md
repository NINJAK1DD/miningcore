# Share relays

Share relay separates public Stratum servers from a central recorder. It is an advanced deployment
model: most pools should run Miningcore and PostgreSQL on one protected Linux host unless load or
network placement requires separate roles.

The repository includes a complete, CI-validated
[Bitcoin sender/recorder example pair](../examples/README.md#distributed-recorder-layout). Read this
guide before populating or deploying those files.

## Roles

- A **relay sender** accepts miners and publishes validated ordinary shares through `shareRelay`.
  Correlated merged-mining projections travel as one envelope, never two independent messages.
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

An accounting envelope is atomic only after the receiver commits it: transport loss can discard the
whole envelope, but cannot commit one chain without the other. Upgrade and migrate receivers before
senders, then stop old senders before enabling PPS or pooled merged mining. New senders use a new
protobuf wire discriminator; old receivers reject it, and new receivers reject accounting fields in
legacy frames. This is deliberately fail-closed rather than silently downgrading financial data.
The receiver validates paired projections against every configured pool, not only pools whose daemon
startup has completed, so a slow auxiliary daemon cannot make a valid envelope disappear during
receiver startup. The topic pool must still be online for normal telemetry attribution. Monitor
`miningcore_share_relay_unsupported_wire_format_total`; any increase means a sender/receiver version
mismatch is rejecting frames and requires immediate rollout correction.

## Recommended bind mode

In the usual topology, each sender binds one protected relay endpoint and the central recorder
connects to it. Generate a different long random secret for each sender/receiver relationship and
keep the port private to the participating hosts.

For example, generate a secret and copy the complete single-line output into both configurations:

```console
openssl rand -base64 32
```

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

The built-in CURVE configuration encrypts relay traffic and allows the receiver to authenticate the
sender. It does not authenticate or authorise receiver clients on the sender because Miningcore does
not configure a ZeroMQ Authentication Protocol (ZAP) handler or client-key allowlist. Any client that
knows the sender's CURVE public key and can reach the endpoint may subscribe without the original
`sharedEncryptionKey`. Restrict the relay port to the intended receiver hosts with host and network
firewalls or a private VPN. Use an operator-managed ZeroMQ proxy with client authentication when the
sender must also verify receiver identity.

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
7. If merged mining is enabled, verify synchronous parent/auxiliary block persistence, one paired
   ordinary envelope, the required migrations, and replay suppression at the receiver.

The repository includes `scripts/regtest/validate-physical-relay.sh` for a final physical-path check.
Its arguments and PostgreSQL environment variables are described in the
[merged-mining validation plan](merged-mining-litecoin-dogecoin.md#pre-production-validation).
