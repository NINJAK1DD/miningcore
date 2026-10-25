# Alephium devnet target-recovery validation

On 25 October 2026, PR #219's candidate-target fix passed a real block submission
against an isolated Alephium **v4.7.3** devnet, network ID **4**. The test used
production `AlephiumJob` initialization, native BLAKE3 hashing, worker issuance,
share validation, credit and block serialization. The harness invokes the internal
worker-snapshot method through reflection; hashes and daemon responses are real.

The node's compact target was `20ffffff`, with an unsigned 24-bit mantissa.
Its exact expanded target begins `ffffff00`; Bitcoin compact re-encoding lowers
that to `ffff0000`. The search requires a proof **above the old rounded target
and at or below the exact consensus target**, with the correct chain index.
This is a block the former implementation would have missed.

The final repeatable run found a proof after 49,763 attempts (nonce counter
49,762), for chain 0 to 0 at height 1:

- Nonce: `00000000000000000000000000000000000000000000c262`.
- BLAKE3 block hash: `ffffc7c46221dec1...b864e984d993f60` (full value in the receipt).
- Issued difficulty: 200 times the candidate ceiling.
- Credited difficulty and candidate ceiling: both `9.31322630126633e-10`.
- The version-1 mining API acknowledged this exact hash as accepted.
- A REST lookup then returned the same persisted block at height 1.

The [captured receipt](fixtures/alephium-devnet-2026-10-25.json) contains the
full template, nonce, both targets and persisted block. Two earlier commissioning
runs also submitted recovered blocks successfully. These devnet receipts do not
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

The nonce search and acknowledgement wait are bounded. An absent recovery
window, rejected block, wrong credit or missing persisted block fails the run.
Evidence includes the Miningcore assembly SHA-256, logs and full JSON receipt.
This opt-in smoke test is separate from required CI native/PostgreSQL coverage;
it submits one freshly mined block on a disposable private network.

Reference encoding and admission:
[Alephium Target](https://github.com/alephium/alephium/blob/v4.7.3/protocol/src/main/scala/org/alephium/protocol/model/Target.scala),
[Alephium PoW](https://github.com/alephium/alephium/blob/v4.7.3/protocol/src/main/scala/org/alephium/protocol/mining/PoW.scala).
