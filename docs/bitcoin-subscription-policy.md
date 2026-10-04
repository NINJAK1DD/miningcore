# Bitcoin-family subscription compatibility

Each TCP connection served by `BitcoinPool` has one successful `mining.subscribe`.
A stray duplicate receives
Stratum error `20` (`Other`) and `result: false`. The connection remains usable and its
outstanding jobs remain valid. Another duplicate on that connection closes the transport
without another response, including requests already buffered by the receive loop.

Rejection happens in dispatch before subscription overrides, parameter conversion,
extranonce allocation, NiceHash lookup or work publication. It preserves extranonce,
user agent, difficulty, pending VarDiff,
version-rolling mask, authorization and the job registry. A duplicate never sends
`mining.set_difficulty` or `mining.notify`, and cannot change a direct-SOLO payout binding.
The warning allowance lasts for the connection; successful authorization, accepted shares,
difficulty changes and elapsed time do not reset it.

Missing or null request IDs retain error `-1` precedence and do not consume this warning.
This duplicate-subscription policy bounds identified duplicate attempts; general malformed
traffic and cross-connection churn are separate transport/admission concerns. See
[connection admission](stratum-connection-admission.md).

## Protocol and proxy scope

| Protocol or operation | Behavior |
| --- | --- |
| Canonical Bitcoin and templates served by `BitcoinPool` | One subscription per connection; one duplicate warning, then disconnect |
| Litecoin, Dogecoin, Bitcoin Cash and inherited merged-mining Stratum | Same `BitcoinPool` subscription policy |
| Bitcoin BLAKE2b header-v2 | Existing identical duplicate policy; its separate difficulty budget remains unchanged |
| Initial subscribe with an optional proxy/session-resume parameter | Accepted as before; a fresh connection receives a fresh unique extranonce |
| `mining.extranonce.subscribe` | Remains a separate supported extension; it does not repeat `mining.subscribe` or allocate another extranonce |
| Configure, version rolling, ordinary authorization and server VarDiff | Continue through their existing handlers; BIP310 uses the latest negotiated connection mask |
| `SatoshicashPool`, `NexaPool`, `HandshakePool` | Independent subscribe handlers still accept duplicates and can rotate extranonce; this PR does not fix those paths |
| `EquihashPool`, `ProgpowPool`, `KaspaPool` and other independent protocols | Separate handlers and work formats; no duplicate-subscription safety claim from this PR |

The related [independent dispatcher audit](https://github.com/NINJAK1DD/miningcore/issues/192)
covers response publication outside Bitcoin. Its existence does not establish duplicate-subscribe
safety for these independent handlers. Issue #181's implemented scope is `BitcoinPool`, its
inherited merged-mining dispatcher, and the existing BLAKE2b policy.

Proxies should subscribe once on each upstream connection and share that upstream assignment
according to their existing downstream extranonce allocation. Additional downstream miners
must not trigger another upstream subscribe. To replace the upstream subscription, reconnect,
subscribe and authorize again, then use the fresh assignment. The optional resume parameter
does not promise resumption of jobs from a closed connection.

A client may continue submitting its outstanding work after the first error. Repeated retry
loops now disconnect deliberately. Test firmware/proxies that treat every protocol error as
fatal or repeatedly subscribe on an isolated endpoint before upgrading the pool.

The server rejects instead of replaying subscription success to match the existing BLAKE2b
policy and make the retry visible without suggesting that it assigned new work. One warning
preserves a producing connection. Clients that treat any subscribe error as fatal may reconnect.
The second duplicate uses the existing abortive transport close: queued replies, including a
prior accepted-share acknowledgement, can be lost. A missing acknowledgement does not imply
proof rejection; already admitted accounting retains its existing ownership and is not replayed.

The policy keeps `mining.extranonce.subscribe` distinct from initial subscription as defined
by the [NiceHash extension specification](https://github.com/nicehash/Specifications/blob/master/NiceHash_extranonce_subscribe_extension.txt).
The [Slush proxy implementation](https://github.com/slush0/stratum-mining-proxy/blob/master/mining_proxy.py)
also establishes its upstream extranonce assignment using subscribe during connection setup.
Neither flow requires rotating extranonce through a duplicate in-session subscribe. These
sources support the compatibility design; the regression fixtures simulate protocol flows
and do not certify every deployed proxy or firmware version.

## Direct coinbase and accounting

[Direct-SOLO](bitcoin-direct-solo.md) still withholds work until network-aware address
authorization succeeds. Authorize-before-subscribe and subscribe-before-authorize remain
supported. A duplicate before authorization cannot issue a job. A duplicate after
authorization preserves the exact immutable authorization generation and job payout script.
In-flight submissions keep their persistence ownership and receive one acceptance only after
admission completes. A legitimate later reauthorization retains its existing generation and
submission-gate rules.

The first successfully enqueued warning increments
`miningcore_stratum_admission_total{pool,outcome="duplicate-subscribe-warning"}` once, with no
dedicated log line. Terminal duplicate closure emits one structured `DuplicateSubscription`
event and increments outcome `duplicate-subscribe` once. Both outcomes are fixed allowlisted
values; neither introduces client or request labels. Observer failures are best effort and
cannot invalidate the first warning's preserved work or prevent terminal cleanup.
Logs do not include request parameters, miner payout addresses or passwords. Terminal cleanup
latches transport closure, permanently closes the job registry and then closes I/O before
optional log/metric observers run. Concurrent producers cannot reinsert jobs after cleanup.

## Extranonce and version-mask identity

Duplicate subscribe cannot change extranonce, so an outstanding job and its original session
assignment remain coherent. Job templates can be shared between workers; a worker-specific
extranonce must not be attached to the shared template.

Version rolling has a different protocol contract. [BIP310](https://github.com/bitcoin/bips/blob/master/bip-0310.mediawiki)
requires submit validation and header reconstruction to use the **latest connection mask**,
including on an existing job. Freezing the earlier mask in a job entry would accept bits that
the current negotiated mask forbids and reconstruct a different header from a conforming miner.
A successful later `mining.configure` can therefore change the mask; clients must apply the
returned mask to subsequent submissions. A duplicate `mining.subscribe` never changes it.
The TCP regression checks both rejection of newly forbidden bits and acceptance of outstanding
work calculated with the latest mask. See [version-rolling policy](version-rolling.md).

## Validation

See the [validation record](bitcoin-subscription-validation.md) for the real-TCP baseline
reproduction, live Bitcoin Core results, regression commands and review dispositions.
