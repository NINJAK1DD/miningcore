# Bitcoin-family subscription compatibility

Each TCP connection has one successful `mining.subscribe`. A stray duplicate receives
Stratum error `20` (`Other`) and `result: false`. The connection remains usable and its
outstanding jobs remain valid. Another duplicate on that connection closes the transport
without another response, including requests already buffered by the receive loop.

Rejection happens before parameter conversion, extranonce allocation, NiceHash lookup or
work publication. It preserves extranonce, user agent, difficulty, pending VarDiff,
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
| Configure, version rolling, ordinary authorization and server VarDiff | Continue through their existing handlers |
| Independent coin-family protocols, including Equihash and ProgPoW | Keep their own handlers; this policy applies to the Bitcoin pool dispatchers |

Proxies should subscribe once on each upstream connection and share that upstream assignment
according to their existing downstream extranonce allocation. Additional downstream miners
must not trigger another upstream subscribe. To replace the upstream subscription, reconnect,
subscribe and authorize again, then use the fresh assignment. The optional resume parameter
does not promise resumption of jobs from a closed connection.

A client may continue submitting its outstanding work after the first error. Repeated retry
loops now disconnect deliberately. Commission firmware/proxies that treat every protocol
error as fatal or repeatedly subscribe on a producing upstream connection before upgrading.

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

Terminal duplicate closure emits one structured `DuplicateSubscription` event and increments
`miningcore_stratum_admission_total{pool,outcome="duplicate-subscribe"}` once. The first warning
has no dedicated diagnostic. Logs do not include request parameters, miner payout addresses
or passwords. Transport closure is latched before optional log/metric observers run.

## Regression and lab evidence for issue #181

The [TCP regression suite](../src/Miningcore.Tests/Blockchain/Bitcoin/BitcoinDuplicateSubscriptionTests.cs)
uses the production dispatcher, subscription/job creation and share validation over real
newline-delimited TCP. The ordinary fixtures substitute persistence and background template
acquisition. They cover original-proof credit, representative Bitcoin/LTC/DOGE/BCH assignments,
proxy resume parameters, extranonce extension, ASICBoost response shape, malformed duplicates,
missing/null IDs, independent connections, a pipelined repeated-subscribe burst, direct payout
binding and delayed persistence admission.

Before the fix, the original-proof test failed against `34af171c7`: the first subscription
assigned `f0000001`, the second assigned `f0000002`, and the coinbase transaction hash changed
from `f45e61a56a3db6ff201d6c4aa3eba04c3b4167662524e55b7183544236453021` to
`0bc28fd581ad8ddb924efec27fef10f91453387d47fb5d9e7d80141c4882aa46`. An outstanding
proof with nonce `0000003c`, valid at difficulty `1e-7` under the original assignment, was
rejected with error `23` and reconstructed difficulty `1.0937091875612106E-09`. These are
recorded observations from a generated fixture, not fixed consensus vectors. The same test
now accepts the original proof and retains extranonce, job count and accounting identity.
To repeat the before/after comparison, copy the regression test file into an isolated checkout
of that baseline and run `OutstandingCanonicalProof_RemainsValidAfterDuplicateSubscribe`.

Validation on 2026-10-04:

- Windows .NET 10: 954 relevant Bitcoin-family/transport cases passed; 17 optional external
  integration cases were explicitly skipped. The managed-only test build used
  `-p:BuildOdoCryptWindows=false`; this change does not modify native hashing code.
- Documented Ubuntu 22.04 WSL compatibility lab: all 21 selected duplicate-subscription,
  publication-cleanup, direct-SOLO and version-rolling cases passed, with no skips. The lab ran
  the rebuilt managed test assembly with its existing Linux native libraries, in isolated test
  output. Active pool services, wallets and database configuration were not replaced.
- The new daemon-backed TCP case used Bitcoin Core 28.1, with the official Linux archive
  verified against the repository CI SHA-256 pin
  `07f77afd326639145b9ba9562912b2ad2ccec47b8a305bd075b4f4cb127b7ed7`. Core accepted
  both the outstanding custodial block and direct block after the duplicate warning; decoded
  coinbase IDs and payout scripts matched the original jobs. Persistence is substituted in
  this fixture; it is not a PostgreSQL ledger test.

Run the suite with `dotnet test src/Miningcore.Tests/Miningcore.Tests.csproj --filter
FullyQualifiedName~BitcoinDuplicateSubscriptionTests`. Set `MININGCORE_TEST_BITCOIND` to a
real Core binary to enable the daemon case. Existing publication tests now inject failure
during initial subscription or configure rather than relying on an unsafe second subscription.
