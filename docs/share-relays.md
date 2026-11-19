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
  A sender with PPS pools also runs a local recorder for PPS envelopes; other ordinary shares
  remain the receiver's responsibility.
- A **receiver/recorder** subscribes through `shareRelays`, writes ordinary shares to PostgreSQL and
  is normally the only payout/reconciliation owner.
- A direct node can also subscribe to senders. When `shareRelays` is configured, internal Stratum
  defaults to disabled; set `enableInternalStratum: true` on a pool only when that receiver should
  also accept miners itself.

Only one process may own payouts for a database/pool set. A non-merged relay sender can be
database-free when payment processing is disabled there and no enabled pool uses PPS. A Litecoin–Dogecoin merged-mining sender
must have PostgreSQL because financially significant parent and auxiliary candidates are persisted
synchronously on the submitting node; see the [merged-mining guide](merged-mining-litecoin-dogecoin.md).

## Durability boundary

The ordinary-share relay is ZeroMQ PUB/SUB, not an acknowledged durable queue:

- for statistical, non-PPS shares, a positive Stratum response proves admission to the local in-memory relay queue;
- it does not prove that the receiver or PostgreSQL stored the share;
- shares published while the receiver or route is unavailable are not replayed; and
- sender restart or sudden host loss can discard queued ordinary shares.

The receiver's PostgreSQL queue and recovery journal protect shares only after the receiver has
received them. Monitor the complete route and accept this statistical-share loss boundary before using relays. Merged
block candidates use separate synchronous PostgreSQL/recovery-journal persistence and are not
delegated to ordinary PUB/SUB delivery.

At the receiver, an accounting envelope commits atomically: transport loss can discard the
whole envelope, but cannot commit one chain without the other. Upgrade and migrate receivers before
senders, then stop old senders before enabling PPS or pooled merged mining. New senders use a new
protobuf wire discriminator; old receivers reject it, and new receivers reject accounting fields in
legacy frames. This is deliberately fail-closed rather than silently downgrading financial data.
The receiver validates paired projections against every configured pool, not only pools whose daemon
startup has completed, so a slow auxiliary daemon cannot make a valid envelope disappear during
receiver startup. The topic pool must still be online for normal telemetry attribution. Monitor
`miningcore_share_relay_unsupported_wire_format_total`; any increase means a sender/receiver version
mismatch is rejecting frames and requires immediate rollout correction.

All hosts participating in accounting must use synchronized UTC clocks. Evidence more than five
minutes in the future is rejected, and the recorder re-evaluates the replay horizon immediately
before its database transaction. Keep
`paymentProcessing.shareAccountingRetentionDays` identical on senders, receivers, recorders and the
payout owner; mismatched values can cause one host to reject evidence another still considers live.
Every PPS sender also requires PostgreSQL, local share partitions and both candidate/accounting
migrations. Before acknowledging a PPS share, its local recorder must commit the share receipt,
liability and balance atomically, or force-flush the authenticated recovery journal and terminal
anchor. This applies while local payouts are paused and to either PPS projection in a paired
envelope. Failed database and journal durability closes admission, suppresses positive responses
and retains a startup-blocking incident; database recovery alone cannot clear it. A lost commit
response remains uncertain and requires reconciliation.

PPS envelopes still travel through the relay. Senders and receivers must use the **same accounting
database**: immutable receipt identities deduplicate those writes. Separate databases sharing a
wallet are not one accounting or emergency-stop domain. Do not deploy a PPS relay sender without
its local recorder or rely on PUB/SUB delivery as proof of a liability. An accepted direct block
also retains its separate synchronous candidate-persistence boundary and protected journal fallback.

## Required PPS transport protection and upgrade

An enabled pool configured with the PPS scheme requires a bind-mode publisher and a
`sharedEncryptionKey` of at least **32 characters after trimming whitespace** on the sender
and every receiver endpoint. This check also applies when that pool's payment processing
is disabled. Generate at least 32 random bytes, as below; length alone is not entropy.
PPS connect-mode senders are rejected even if an external VPN or broker protects the link.

Before upgrading an existing PPS relay, inventory all participating versions and keys,
stop admission, and rotate `sharedEncryptionKey` on the publisher and its receivers if
older logs containing the CURVE public key exist. Earlier versions logged that public
key; anyone with it and network access could subscribe and read addresses, workers and IPs
without knowing the secret. The public key does not permit publisher impersonation, but
removing old log lines does not revoke a copy. Rotate the secret to change the publisher
identity, protect retained logs, restrict listener access and verify the new route before
resuming admission. Do not reprint either key in incident records.

Set the originating `clusterName` before admitting work. Source identity is part of the
accounting receipt. Pearl records it before its first durable commit; relay serialization
uses a separate copy and preserves identified financial envelopes, including historical
receipts whose source is null. Republishing from another cluster must not rewrite them.

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
  "sharedEncryptionKey": "CHANGE_ME_RELAY_SHARED_SECRET"
}
```

Receiver/recorder:

```json
"shareRelays": [
  {
    "url": "tcp://192.0.2.20:5555",
    "sharedEncryptionKey": "CHANGE_ME_RELAY_SHARED_SECRET"
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

This connect-mode option is available only for non-PPS deployments. A configured PPS
route must use the protected bind-mode arrangement above.

## Bounded transport, fail-stop and monitoring

**All relay senders, including PPLNS/PROP, stop mining admission** on an oversized frame,
serialization failure, local queue saturation or publisher exception. The rejected share
cannot receive a positive response. This intentionally avoids continued admission through
a known broken route: missing ordinary PPLNS/PROP shares can affect payout weighting.
Preserve recovery evidence and restore the route before restarting; ordinary network copies
lost before failure are not automatically replayed.

The local publisher queue holds at most **4,096 messages / 16 MiB of serialized payload**,
with a **1 MiB per-frame limit**. PUB and SUB native high-water marks are **128**, and the
receiver's managed native-message queue is **128**. Compared with ZeroMQ's usual 1,000-message
native default, these bounds reduce memory exposure but can increase silent loss during
ordinary-share bursts. They are queue bounds, not a total-process-memory guarantee.

Monitor these publisher series separately from receiver health:

- `miningcore_share_relay_queue_depth` and `miningcore_share_relay_queue_capacity`;
- `miningcore_share_relay_queue_high_watermark`;
- `miningcore_share_relay_queue_bytes` and `miningcore_share_relay_queue_byte_capacity`;
- `miningcore_share_relay_queue_rejected_total`.
- `miningcore_share_relay_accepted_total` and `miningcore_share_relay_native_send_total`.

The two totals count successful local enqueue and native PUB send, respectively. Neither
is a receiver acknowledgement. Native congestion or a missing subscriber can discard shares
while both totals agree and the rejection counter remains zero. The native-loss integration
test compares these boundaries with real recorder rows in PostgreSQL after a FIFO tail marker
has persisted; it covers PPLNS and PROP with a stalled receiver and no receiver at all.

For a dedicated ordinary-share route, assign the same `relay_group` scrape label to its senders
and its **one designated** receiver, and separate them with `role=relay_sender/relay_receiver`.
An example discrepancy alert is:

```yaml
- alert: OrdinaryRelayDeliveryDiscrepancy
  expr: >-
    sum by (relay_group) (increase(miningcore_share_relay_accepted_total{role="relay_sender"}[10m]))
    - sum by (relay_group) (increase(miningcore_valid_shares_total{role="relay_receiver"}[10m])) > 10
  for: 5m
  labels:
    severity: critical
  annotations:
    summary: "Accepted relay shares exceed receiver observations; inspect durable accounting"
- alert: OrdinaryRelayReceiverMetricsMissing
  expr: >-
    count by (relay_group) (miningcore_share_relay_accepted_total{role="relay_sender"})
    unless on (relay_group)
    count by (relay_group) (miningcore_valid_shares_total{role="relay_receiver"})
  for: 1m
  labels:
    severity: critical
```

Choose the count/window from the pool's financial loss budget and observed scrape/processing
delay; 10 shares is only an example. Also alert on failed scrapes (`up == 0`), recorder recovery
or fail-stop, and queue pressure. Receiver telemetry precedes persistence and must not be
treated as durable evidence. During an alert, stop the affected ingress, drain healthy queues,
and compare the sender totals for the incident interval against PostgreSQL shares grouped by
pool/source, preserving timestamps and the counter-reset history. Account for block-only and
merged-mining envelopes separately; the example is not valid for mixed routes or broadcast
receivers without adapting the expected multiplicity. Do not automatically repay, synthesize
missing shares, or clear an incident because telemetry alone matches.

Operating PPLNS/PROP through PUB/SUB requires explicit operational acceptance of this lossy
boundary in the deployment record. If missing weight-bearing shares is unacceptable, keep
their recorder local to the ingress and do not deploy this remote-recording topology. Durable
receiver acknowledgement/replay is not supplied by the emergency wallet-stop feature.

Alert on rising occupancy and any rejection. Also compare sender accepted-share rates with
receiver `miningcore_valid_shares_total` rates per pool, inspect recorder queue/recovery
state, and reconcile accounting receipts/credits in the shared database. An empty publisher
queue proves neither network delivery nor receiver processing. PPS local receipts protect
liabilities; they do not make receiver-side telemetry complete. PPLNS/PROP weighting retains
the documented ordinary-share loss boundary.

Relay serialization is serialized before allocating its snapshot and payload buffers. Concurrent
producers therefore cannot each allocate a maximum-size intermediate payload while waiting for
the queue lock. The 16 MiB limit still covers only queued payload bytes: one active serializer,
one native send, native socket queues, caller-owned shares and the rest of Miningcore consume
additional memory. The concurrent near-limit regression reports process peak resident memory,
allocated managed bytes and GC collection deltas; these are observations, not a memory SLA.
Monitor process/container resident memory separately from queue occupancy and alert at 80% of
the memory budget established in staging, with a critical alert before the container/host OOM
limit. Include sustained GC pressure and increasing submission latency in that alert policy.
Do not use the 16 MiB queue limit as the container memory limit.

For PPS pools, alert on submission latency as well as queue pressure. The existing
`miningcore_stratum_request_execution_time` summary measures complete request duration in
milliseconds, including validation and the acknowledgement durability wait. For example,
adapt this five-minute **mean** latency alert to your pool IDs and workload:

```yaml
- alert: PpsSubmitLatency
  expr: >-
    rate(miningcore_stratum_request_execution_time_sum{pool=~"pps-pool-a|pps-pool-b",method="mining.submit"}[5m])
    / rate(miningcore_stratum_request_execution_time_count{pool=~"pps-pool-a|pps-pool-b",method="mining.submit"}[5m]) > 250
  for: 5m
  labels:
    severity: warning
  annotations:
    summary: "PPS submit latency exceeds the configured operating threshold"
```

Use the protocol's actual submit method label for other coin families. This is not a p99
or pure database-latency measurement; native proof verification can dominate Pearl requests.
The measured capacity p95/p99 values and hardware limitations are in the
[wallet-stop evidence](emergency-wallet-stop.md#measured-pps-capacity). Validate thresholds
on the deployment hardware rather than treating the example as a universal SLA.

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
