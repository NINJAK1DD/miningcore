# Alephium devnet exact-target validation

On 25 October 2026, PR #219's candidate-target fix passed a real block submission
against an isolated Alephium **v4.7.3** devnet, network ID **4**. The test used
production `AlephiumJob` initialization, native BLAKE3 hashing, worker issuance,
share validation, credit and block serialization. The harness invokes the internal
worker-snapshot method through reflection; hashes and daemon responses are real.

The node's compact target was `20ffffff`, with an unsigned 24-bit mantissa.
Its exact expanded target begins `ffffff00`; Bitcoin compact re-encoding lowers
that to `ffff0000`. The search requires a proof **above the old rounded target
and at or below the exact consensus target**, with the correct chain index.
This is a valid block that intermediate commit `97ffa085` (exact hash but still
rounded target) would have missed. Pre-PR `dev` rounded both the proof and target,
so it also classified this proof as a candidate. The final PR preserves that
valid classification and eliminates false-positive candidates **above the exact
target**. The receipt is evidence of exact-boundary correctness and real daemon
acceptance, not recovery of a block missed by pre-PR `dev`.

| Implementation | Candidate rule | Recorded valid proof |
| --- | --- | --- |
| Pre-PR `dev` | `round(hash) <= round(target)` | Candidate |
| Intermediate `97ffa085` | `hash <= round(target)` | Missed |
| Final PR | `hash <= target` | Candidate |

The harness computes all three classifications for the mined proof and checks
that `target + 1` was a legacy false positive but is ordinary under the exact
rule. That boundary check is arithmetic; it is not another mined or submitted
block.

The provenance-bearing repeatable run found a proof after 774,140 attempts (nonce counter
774,139), for chain 0 to 0 at height 1:

- Nonce: `0000000000000000000000000000000000000000000bcffb`.
- BLAKE3 block hash: `ffffc8af201aa194...0d9b5e42c84b1da0` (full value in the receipt).
- Issued difficulty: 200 times the candidate ceiling.
- Credited difficulty and candidate ceiling: both `9.31322630126633e-10`.
- The version-1 mining API acknowledged this exact hash as accepted.
- A REST lookup then returned the same persisted block at height 1.

The [captured receipt](fixtures/alephium-devnet-2026-10-25.json) contains the
full template, nonce, both targets, computed classifications and persisted block.
Its `issuedDifficulty` records the actual immutable worker assignment alongside
the capped credit. Its `build` object records the loaded Miningcore assembly's source commit,
informational version and SHA-256. These identify the tested product binary,
not the later commit that adds this receipt. Two earlier commissioning runs
also submitted valid rounding-window blocks successfully. These devnet receipts do not
establish mainnet operation or ASIC/firmware compatibility.

## Reproduce

Use the Linux native build/test lab with .NET 10, Docker and curl, under an
unprivileged user permitted to run Docker. Build Miningcore and its native
libraries first, then run from the repository:

```sh
bash scripts/tests/alephium-devnet-smoke.sh
```

For an existing lab build or a separate evidence directory:

```sh
MININGCORE_OUTPUT=/absolute/path/to/Miningcore/bin/Debug/net10.0 \
  bash scripts/tests/alephium-devnet-smoke.sh /absolute/path/to/evidence
```

The [runner](../scripts/tests/alephium-devnet-smoke.sh) pins the official node
image SHA-256: `c8bc624a166f7328c244f975d9fe5581609fe5a66f09533ff7115b39ffa80351`
and checks its reported version. It creates a fresh internal Docker network with
no published ports, bootstrap peers, UPnP or wallet service. Private host access
is used, and network ID 4 is checked before any submission. CPU and memory are
bounded and privileges dropped. The container, anonymous volumes, network and
temporary harness directory are removed on exit. The image and evidence remain.

The nonce search and acknowledgement wait are bounded. An absent rounding
window, rejected block, wrong credit or missing persisted block fails the run.
The cleanup trap captures node logs on success and failure before removing the
container, and preserves the original exit status. Harness stderr is retained
in `run.log`. Evidence includes the loaded Miningcore assembly source commit and
SHA-256 inside `result.json`, plus logs and the separate assembly hash file.
For an offline native replay of a receipt, build the harness against Miningcore
and run `AlephiumDevnet.dll --verify-receipt /absolute/path/to/result.json`.
Replay recomputes BLAKE3, chain selection and both recorded classification checks.
It requires `accepted == true`, cross-checks the persisted block hash, nonce,
height and chain against the proof/template, and verifies the recorded 200-times
issued assignment and capped credit;
it validates provenance field format, not availability of the historical binary.
The live Docker smoke test remains opt-in and submits one freshly mined block
on a disposable private network. CI runs the offline native receipt replay and
historical boundary checks alongside required native/PostgreSQL coverage.

Reference encoding and admission:
[Alephium Target](https://github.com/alephium/alephium/blob/v4.7.3/protocol/src/main/scala/org/alephium/protocol/model/Target.scala),
[Alephium PoW](https://github.com/alephium/alephium/blob/v4.7.3/protocol/src/main/scala/org/alephium/protocol/mining/PoW.scala).
