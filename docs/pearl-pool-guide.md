# Run a Pearl (PRL) mining pool

This guide takes an operator from a working Miningcore installation to a verified
Pearl pool. Pearl needs a proof verifier/prover, a synchronized Pearl node, and an
Oyster wallet. Adding a coin entry alone is insufficient.

The [development reference](pearl-development.md) describes the protocol, security
limits, accounting, and recorded certification evidence. All addresses, passwords,
and hostnames below are placeholders. Never publish a real configuration or wallet
backup when asking for help.

## 1. Prepare the software and wallet

1. Use the certified Linux glibc x64 platform and Pearl **v1.5.5** source revision
   `d7c517d267f10f5bcbbdc366f3e603942f9a5ab5`. Build the node with real `xmss,zkpow`
   support using the [upstream installation guide](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/node/docs/installation.md).
   A verification-bypass build is unsuitable for a pool.
2. Start the mainnet node and let it synchronize. Its default RPC and P2P ports
   are 44107 and 44108. Keep RPC private; it is not a miner endpoint.
   `--txindex` is optional: confirmed payout recovery can verify canonical block
   membership when transaction lookup is unavailable without the index.
3. Set up [Oyster](https://github.com/pearl-research-labs/pearl/blob/d7c517d267f10f5bcbbdc366f3e603942f9a5ab5/wallet/README.md)
   against that same node. Back up the wallet using its documented procedure.
   Obtain a `prl1p...` address from Oyster's `default` BIP86 account with
   `getnewaddress`; the pool must own this reward address. Wallet RPC defaults to
   44207 and must also remain private.
4. Build/install Miningcore and the pinned Pearl worker as described in
   [Build and install](pearl-development.md#build-and-install). Confirm that the
   published installation includes `pearl/miningcore-pearl-worker`, its caches,
   checksums, and license notices. Ordinary packages without the optional Pearl
   bundle cannot run this coin.
5. Apply the documented database migrations as the schema owner, after stopping
   writers and taking a backup. Use an ordinary application role for Miningcore.
   Both share-accounting migrations and `add_pearl_candidates.sql` are required.
   Re-run the idempotent Pearl migration on upgrade: it adds `pearl_payout_holds`,
   candidate archival (`retired`), and the administrator's `pearl_payout_intent_releases`
   audit table. Grant the application role SELECT/INSERT/UPDATE/DELETE on holds and
   candidates; keep intent-release audit writes restricted to the schema administrator.

Allow substantial memory headroom beyond the worker's 12 GiB address-space limit
for Miningcore, the node, wallet, database, and operating system. Only one
certificate prover runs at a time; two verification workers may run concurrently.
GPU hardware is needed by GPU miners, not by Miningcore's verifier service.

Install the complete checksum-verified bundle as a trusted deployment administrator.
The service account must not own or be able to replace the executable, caches,
bundle directory or any parent directory. The installer uses 0755 directories,
0644 data and a 0555 executable; set ownership to your trusted deployment account
after installation. Startup rejects a symlink executable or a group/other-writable
executable or immediate parent. These checks do not replace secure ownership of
the entire path. Keep wallet files inaccessible to the pool/proof-worker account.

## 2. Enable PRL in Miningcore

Copy [examples/pearl_pool.json](../examples/pearl_pool.json) to a private operator
configuration. It is a complete example; replace **every** `CHANGE_ME` value.
Merge its pool entry into an existing installation rather than replacing unrelated
pools or persistence settings.

| Setting | What to enter |
| --- | --- |
| `pools[].enabled` | `true` |
| `pools[].coin` | `pearl` (the shipped PRL catalogue entry) |
| `pools[].network` | `mainnet`; use a separate installation/data set for `regtest` |
| `pools[].address` | The Oyster-owned `prl1p...` pool reward address |
| `pools[].proofWorker` | Absolute path to the installed, pinned worker executable |
| `pools[].maximumConnections` | Per-pool session cap; default 1024, range 16–4096 |
| `pools[].maximumProofFrames` | Concurrent authorized buffers larger than 32 KiB; default 4, range 1–32 |
| `pools[].daemons` | Private node RPC endpoint and its credentials |
| `pools[].daemons` with `category: "wallet"` | Private Oyster RPC endpoint and its credentials |
| `pools[].ports` | Miner ports, difficulty settings, and the server TLS PFX certificate |
| `persistence.postgres` | The application database connection |
| `paymentProcessing` at cluster and pool levels | Enable both when ready to pay rewards |

For the initial smoke test with the example's PPLNS scheme, set both
payment-processing `enabled` flags to `false`. PPS instead requires both enabled
before accepting work; complete [PPS setup](#pps-setup-and-verification) first.
Keep the node and wallet configured so startup can check synchronization, network,
and ownership. Start the published executable with your private configuration:

```bash
chmod 600 /etc/miningcore/pool.json
/opt/miningcore/Miningcore -c /etc/miningcore/pool.json
```

The example exposes TLS miner ports 4400 and 4401. Install a certificate for your
pool's DNS name and replace the PFX path/password. Bind database, RPC, metrics,
and administrative listeners to loopback or an appropriately restricted network.
Remote RPC connections require verified TLS; never disable certificate checks as
a workaround. Open only the intended miner ports to the public.
Size the existing [per-IP connection admission](stratum-connection-admission.md)
rate, burst and concurrent limits for your miners/NATs. Idle sessions do not
reserve large proof buffers; exhausting one pool's limits does not consume another
pool's transport budget. Increasing these settings requires additional memory
and load testing, not simply raising the prover concurrency.

## 3. Connect a miner

Use the exact certified releases and profiles below. Download only from their
official release pages and verify the publisher's checksums before execution.
Different releases, kernels, GPUs, matrix sizes, and merge-mining modes require
separate validation. A successful connection or a displayed hashrate alone does
not prove pool compatibility.

### SRBMiner-MULTI 3.7.3: tested GPU path

Download [SRBMiner-MULTI 3.7.3](https://github.com/doktor83/SRBMiner-Multi/releases/tag/3.7.3).
Replace the endpoint and wallet, then run:

```bash
./SRBMiner-MULTI --algorithm pearlhash \
  --pool pool.example.com:4400 --tls true \
  --wallet prl1pYOUR_MINER_ADDRESS.rig1 --password x \
  --disable-cpu --gpu-id 0
```

Use the miner's tested default dense profile: `k=32768`, rank 128, tile 8×16.
The wallet is the miner's payout address, not a wallet password or private key.
Worker names may contain ASCII letters, digits, `_`, and `-`, up to 64 characters.

### BzMiner v100.60: CPU profile

Download [BzMiner v100.60](https://github.com/bzminer/bzminer/releases/tag/v100.60).
For the CPU path, select the bounded dense profile explicitly:

```bash
export BZ_PEARL_WORKLOAD_M=1024
export BZ_PEARL_WORKLOAD_N=1024
export BZ_PEARL_WORKLOAD_K=32768
export BZ_PEARL_WORKLOAD_RANK=128
export BZ_PEARL_WORKLOAD_TILE_H=8
export BZ_PEARL_WORKLOAD_TILE_W=16
./bzminer -a pearl -p stratum+ssl://pool.example.com:4402 \
  -w prl1pYOUR_MINER_ADDRESS --worker cpu1 --ssl-verify \
  --nvidia 0 --amd 0 --intel 0 --cpu 1 --cpu_threads 4
```

Set a suitable low initial/minimum difficulty on a separate TLS port for slower
CPU workers; the example GPU difficulty may produce long waits. Tune from measured
accepted-share intervals, targeting roughly 15 seconds, rather than claimed miner
hashrate. Keep the one-proof-per-second admission limit in mind.

For example, add this entry inside the Pearl pool's `ports` object, using your
existing TLS certificate. This gives CPU miners a practical starting difficulty
and lets VarDiff adjust it:

```json
"4402": {
  "name": "PRL-TLS-CPU",
  "listenAddress": "*",
  "difficulty": 0.001,
  "difficultyGraceSeconds": 15,
  "tls": true,
  "tlsPfxFile": "/etc/miningcore/stratum.pfx",
  "tlsPfxPassword": "CHANGE_ME_CERTIFICATE_PASSWORD",
  "varDiff": {
    "minDiff": 0.0001,
    "maxDiff": 10,
    "targetTime": 15,
    "retargetTime": 90,
    "variancePercent": 30
  }
}
```

For a private issuing CA, BzMiner's tested TLS invocation can use
`SSL_CERT_FILE=/etc/miner/pool-ca.pem` together with `--ssl-verify`. Keep hostname
verification enabled and use the DNS name covered by the certificate.

The tested GPU's BzMiner kernels required a larger profile that exceeded the
pool's bounded prover. GPU use of this BzMiner release is **not certified** by the
CPU test. Do not remove the resource limits to make a larger profile fit. WildRig's
pool-specific Pearl mode is also outside the supported protocol.

## 4. Use an official gateway client with the pool

This is optional for operators using SRBMiner/BzMiner directly. The stock solo
gateway does not identify pool workers or account pool shares. Instead, run the
included [gateway adapter](../scripts/pearl/gateway-adapter.py) on the same machine
and under the same Unix account as the official miner client. It exposes the
official `getMiningInfo` and `submitPlainProof` Unix-socket API and connects to
Miningcore's named-object v2 protocol over verified TLS.

Requires Python 3.12+ and its standard library; the adapter itself needs no Torch,
CUDA, RPC credentials, wallet password, or private key. The official client retains
its own upstream dependencies.

Create a private configuration, replacing paths and placeholders:

```json
{
  "socket": "/home/miner/pearl-gateway/rpc.sock",
  "host": "pool.example.com",
  "port": 4400,
  "wallet": "prl1pYOUR_MINER_ADDRESS.official1"
}
```

```bash
chmod 600 /home/miner/pearl-gateway.json
python3 /opt/miningcore-source/scripts/pearl/gateway-adapter.py \
  --config /home/miner/pearl-gateway.json
```

The socket's immediate parent must be private and owned by this user (mode 0700);
the adapter creates it if absent and sets the socket to 0600. The parent of that
directory must already exist. Configure the official client process with:

```bash
export MINER_RPC_TRANSPORT=uds
export MINER_RPC_SOCKET_PATH=/home/miner/pearl-gateway/rpc.sock
# Start your supported dense-V3 proof-producing client with these settings.
```

Do not start the solo gateway on this socket. All clients connecting to one adapter
use the fixed wallet/worker in its private configuration, with separate upstream
sessions. Run separate adapters/configurations for different workers. The adapter
does not listen on TCP; for remote miners, install it beside each miner and use
TLS to reach the pool. Never forward the local unauthenticated socket publicly.

Certification covers the unmodified upstream Python `MiningClient`, `MiningJob`,
and `PlainProof` APIs, exercised with real BzMiner-generated proofs. It does not
certify every upstream GPU/LLM mining backend. In particular, the vLLM/MoE and FP8
paths remain unsupported. A proof-producing client must use dense V3, `m<=32772`,
`n<=15360`, `k=32768`, rank 128, tile 8×16, and the job's assigned pool target.
Do not substitute the network target from the header's compact bits.

For a private CA, set `ca_file` to its PEM certificate. Server hostname verification
remains mandatory. Only a co-located pool bound to a literal `127.0.0.1` or `::1`
may use the explicit `loopback_plaintext: true` option. TLS is the default.

The returned `target` is an exact 256-bit **pool share target**, represented as a
JSON integer. Use the official Python `MiningJob` class or an arbitrary-precision
JSON implementation; JavaScript `Number` cannot represent it exactly. Return the
same `mining_job` with the proof. Changing its header, target, or certificate
version is rejected. A successful submission means verified, durable pool share
admission; it does not mean that a candidate has already become a network block.
Network block submission and later confirmation happen separately.

With the pinned source checkout and its Python dependencies installed, run the
included compatibility check from that environment:

```bash
python /opt/miningcore-source/scripts/pearl/check-gateway-client.py \
  --upstream /opt/pearl-source \
  --socket /home/miner/pearl-gateway/rpc.sock
```

It uses the unmodified official client to check integer targets and explicit
rejection of tampered/cross-session jobs. It prints only sanitized results. This
protocol check complements, but does not replace, real-miner proof and block tests.

The adapter limits sessions to four, retained jobs to eight, job age to 90 seconds,
and submissions to one per second per session. A stalled connection fails closed.
After an uncertain/disconnected submission, inspect accounting before retrying;
never assume it was accepted. The pool retains its independent duplicate, stale,
proof-size, verifier, queue, and accounting protections.

## 5. Verify operation, then enable payouts

For PPS, complete the reserve and configuration steps in the next section **before
accepting any shares**. Both payment-processing switches must already be enabled.

1. Confirm startup reports a compatible synchronized node and an online Pearl pool.
   Check the expected mainnet height; do not compare a regtest height to mainnet.
2. Connect one worker. Check its accepted-share count and Miningcore's corresponding
   durable share accounting, not only a miner-side “submitted” message.
3. Inspect `miningcore_pearl_*` metrics on the private metrics listener. Watch
   `ShareAccepted`, `ShareInvalid`, `ShareStale`, `NativeFailure`, candidate queue
   depth, proof latency, and template age. A growing queue without accepted blocks
   needs investigation before adding miners.
4. Check candidate acceptance against the node and the pool's block record. Mining
   a mainnet block is probabilistic; use an isolated proof-enabled regtest for a
   controlled full-path test. Never reset a test chain under a live accounting ledger.
5. Choose PPS, SOLO, PPLNS, or PROP as described below; set the payment threshold and Oyster unlock settings.
   Enable payment processing at both configuration levels and restart normally.
   Optional Pearl fee/confirmation fields have defaults; the example makes them
   explicit. Default maturity is at least 101 confirmations. Confirm a small payout
   before increasing exposure. Keep wallet backups current.

## 6. Choose and configure a reward scheme

All four schemes use the same verified Pearl work, durable receipts, Oyster payment
handler, minimum-payment threshold, and transaction reconciliation. SOLO is
custodial: the pool receives the coinbase and later pays the finder through Oyster.
It does not send the coinbase directly to the miner.

| Scheme | When a miner earns a balance | Distribution |
| --- | --- | --- |
| PPS | When a verified share commits, before acknowledgement | Template reward × assigned difficulty / network difficulty × retained reward fraction. Independent of later block success or orphaning. |
| SOLO | When a pool block has verified custody and reaches maturity | The finder receives the block reward after reward-recipient allocations. |
| PPLNS | When a pool block has verified custody and reaches maturity | Recent assigned work, newest first, within `factor` network-difficulty units. The boundary share is partially counted; windows may overlap. |
| PROP | When a pool block has verified custody and reaches maturity | The net reward is proportional to assigned work through the winning share. Those shares are consumed; later shares remain for the next round. |

Assigned difficulty is capped at network difficulty. A lucky proof's achieved
difficulty never increases its reward weight. PPLNS defaults to `factor: 2`; use a
positive numeric factor. A partially filled window distributes only its filled
fraction of the net reward. PROP uses all eligible unconsumed round work.

Edit the existing pool's `paymentProcessing` object in
[`examples/pearl_pool.json`](../examples/pearl_pool.json). Keep the node, wallet,
proof-worker and TLS settings from the earlier steps. For example:

```json
"paymentProcessing": {
  "enabled": true,
  "minimumPayment": 1,
  "payoutScheme": "PPLNS",
  "payoutSchemeConfig": { "factor": 2 },
  "minimumConfirmations": 101,
  "feeRateGrainsPerKb": 10000,
  "walletPassword": "CHANGE_ME_OYSTER_UNLOCK_PASSPHRASE"
}
```

For SOLO or PROP, change `payoutScheme` to the desired name and remove
`payoutSchemeConfig`. For PPS, set `payoutScheme: "PPS"`, remove
`payoutSchemeConfig`, and optionally set `ppsShareRetentionDays: 7`. Also enable
the top-level `paymentProcessing.enabled` scheduler. Start each scheme with a
distinct pool ID, listener port, and ledger history. To change an established
pool's scheme or reward percentages, stop admission, drain candidates and recovery
journals, settle/reconcile its old obligations, then use a new pool ID. Reusing a
round/window or replaying immutable PPS evidence against changed fee settings is
not a supported migration.

### PPS setup and verification

1. Complete the [PPS database prerequisites](pps.md#database-prerequisites) and the
   Pearl migrations in this guide. Every accepting pool needs local PostgreSQL;
   unavailable or incompatible accounting must prevent share acknowledgement.
2. Fund Oyster with a separately monitored **spendable reserve** before opening
   admission. PPS liabilities accrue even when blocks are not found, become
   orphaned, or candidate proving/submission fails. Immature coinbases are not
   available funds. Miningcore has no automatic reserve admission circuit breaker;
   stop new work if the reserve policy cannot be maintained.
3. The reward basis is the job's pool coinbase value, including its template fees,
   in grains (`100,000,000` grains per PRL). Configured reward-recipient percentages
   reduce the miner's PPS basis once. Recipients receive their configured portion
   of actual matured blocks; they are not additionally credited per share.
4. The shared ledger retains liabilities at 24 decimal places, credits balances
   at 12, and carries the remainder per pool/miner. Oyster outputs are floored to
   eight decimal places; smaller unpaid balance remains on the ledger. Minimum
   payment must be at least `0.01 PRL`. Wallet fees and the reconciliation marker
   need additional spendable funds.
5. Leaving `ppsBinary64Activation` absent uses the existing version-0 arithmetic.
   Version 1 is also supported, but requires the matching administrator-created
   database cutover and UTC configuration on all accounting participants. Follow
   the [arithmetic migration runbook](pps-arithmetic-migration.md); do not simply
   add a timestamp to a running pool or edit historical credits.
6. In an isolated proof-enabled lab, confirm one accepted share creates one
   `pps_share_credits` row and the matching balance/remainder before maturity.
   Confirm recorder replay does not duplicate it. Verify an Oyster payment by its
   transaction outputs and ledger debit, then restart and verify no second payment.
   A pre-maturity orphan leaves the PPS liability intact. A post-settlement reorg
   quarantines the affected block and holds new payments for manual reconciliation.

For SOLO, PPLNS and PROP, verify no miner allocation before maturity, the expected
finder/window/round allocation afterwards, and no second allocation on restart.
The automated PostgreSQL suite exercises unequal assigned work, reward fees,
consecutive rounds, window boundaries, concurrent settlement, and wallet output
and ledger reconciliation for all four schemes.
The [verification report](pearl-reward-schemes-evidence.json) also records real
proof-enabled chain mining, mature payouts and paid-ledger restart checks for each.

## 7. Recover held work and payments

Keep recovery evidence private. Stop every pool/payment writer for the affected
database, prove the recorded payout owner is no longer running, and back up the
database, recovery journal and original Oyster wallet before an administrator
changes financial state. Follow [payout-owner lease recovery](database.md) for the
exact owner/generation and advisory lock. Never run two payout owners to work
around a hold. Record the incident, approver, exact pool/block/marker identities,
ledger snapshots, chain evidence and actions in an access-controlled audit record.

### Temporary chain or RPC failure

Restore connectivity and allow the same handler to retry. Above-tip confirmed
rewards acquire durable holds; catching up in height alone is insufficient.
Classification must verify the original block's canonical identity on a stable
chain before clearing the hold. Previously submitted payout intents can still
reconcile while these holds prevent new sends. Do not delete `pearl_payout_holds`.
Up to four configured node endpoints may fail over at operation boundaries;
each must have the correct genesis, and new payments require agreement with the
wallet tip. An unsupported template upgrade stays paused until a compatible
template is obtained, even if later RPC calls fail.

### Candidate saturation or expired recovery

The active queue holds 16 candidates **per pool**. A capacity or database refusal
does not acknowledge work or retain its in-flight replay reservation. A miner may
retry the same proof while its original session/job remains valid. The receipt,
PPS liability and candidate remain one atomic operation, including ambiguous
commit recovery. Never manufacture credits for rejected work.

Investigate repeated saturation alerts immediately: verify the worker, CPU/memory
limits and RPC availability. The pool completes known side-chain/rejected blocks
as orphaned records. Unresolved candidates older than 24 hours retain all evidence
with `retired` set, freeing active capacity. Administrators should inspect archived
rows privately. For a serialized block, verify the stored header/hash and exact
canonical chain state before any submission or ledger repair. For unproved work,
verify that its parent is still current. Do not clear `retired` blindly or delete
evidence; an accepted archived block needs a reviewed, idempotent reconciliation
into the block ledger, not another share/PPS credit.

### Funding or fee preflight failure

Keep `feeRateGrainsPerKb` at least **1000** (default **10000**). Fund recipients plus the
0.01 PRL wallet-owned marker and the calculated fee reserve; the reserve is at
least another 0.01 PRL and grows with the configured rate and wallet UTXO count.
A preflight shortfall creates no payout intent, so ordinary retries resume after
funding. If fragmentation exceeds the bounded transaction estimate, consolidate
under your wallet administration procedure, then retry. An error **after** intent
creation follows the reconciliation procedure below.

### An intent exists, but the send result is uncertain

1. Inspect the exact `pearl_payout_intents` row and its original wallet. Use
   `listaddresstransactions([marker],"*")` and `gettransaction(txid)`; verify the
   raw transaction, every recipient amount, marker, owned change and fee. Preserve
   the wallet and node evidence. A missing marker result from an unsynchronized,
   replaced or incomplete wallet proves nothing.
2. If a transaction exists, retain the intent and restore node/wallet access so
   Miningcore can reconcile it. Confirmed transactions do not require `txindex`:
   the handler verifies membership in the canonical block on a stable tip.
   Unknown unconfirmed transactions may be rebroadcast as the **same bytes**.
   Never manually call a new `sendmany` or mark the balance paid.
3. If transaction absence cannot be established conclusively, retain the hold.
   Oyster RPC errors alone are insufficient: the same error category can occur
   after attempted publication. Adding funds does not make a second send safe.
4. Only a schema administrator with a documented, approved **no transaction was
   created or broadcast** determination may release an unused intent. This needs
   authoritative original-wallet/process evidence of the pre-creation failure,
   corroborated wallet history, mempool/chain checks and unchanged payment ledger.
   An empty lookup by itself does not satisfy that requirement. Preserve the
   approved evidence privately and compute its SHA-256.
5. With writers stopped and the dead-owner lease handled, run the
   [guarded script](../scripts/ops/release_pearl_payout_intent.sql)
   using placeholders replaced privately (use your normal secure database login):

   ```bash
   psql -v pool=POOL_ID -v marker=EXACT_UNUSED_MARKER \
     -v audit_sha256=SHA256_OF_APPROVED_PRIVATE_AUDIT \
     -v reason='Approved audit establishes failure before transaction creation' \
     -v acknowledge=NO_TRANSACTION_CREATED \
     -f scripts/ops/release_pearl_payout_intent.sql
   ```

   The script locks the exact intent, refuses any known transaction ID, archives
   its recipients/time plus administrator, reason and audit hash, then removes
   only that intent atomically. It neither pays nor changes balances. It cannot
   establish transaction absence for you. Review the resulting audit row and
   unchanged ledger before restarting one owner. The next normal payment creates
   a fresh marker and must reconcile to one actual transaction.

### A settled reward is quarantined

Quarantine means balances may already include a reward that lost canonical
backing, or a historical round/window requires accounting review. It is a financial
hold, not an ordinary stale job. Record the exact block, coinbase, prior allocation,
PPS credits, balances and any payments; never reverse paid money automatically.

For a previously confirmed block whose **same hash is restored**, independently
verify canonical `getblockhash(height)`, stable node/wallet tips, exact wallet-owned
coinbase and at least the configured confirmations. An administrator may then,
with writers stopped and an approved audit, change only that exact already-settled
row from `quarantined` back to `confirmed` and set confirmation progress to 1.
Use a transaction, row lock and predicates for pool ID, database block ID, original
hash and current `quarantined` status; require exactly one affected row. Preserve
balances, payments, share windows, reward and any durable hold. On restart the
handler must verify the original canonical identity before removing the hold;
restoration to `confirmed` cannot run allocation a second time. Record before/after
rows and audit approval privately.

Do **not** use that procedure for a permanently invalid reward or an unsettled
historical-window quarantine. There is no generic safe status flip for those
cases: keep payments held while an authorized financial reconciliation determines
liabilities, external payments and reserve replenishment. Any ledger correction
requires a separate reviewed migration and evidence that no reward can be credited
or paid twice.

## Troubleshooting

| Symptom | Check and action |
| --- | --- |
| Pool fails before accepting miners | Check the pinned worker bundle/version, executable permissions, database migrations, RPC connectivity, genesis/network, node synchronization, and wallet ownership. |
| PPS pool refuses startup | Enable payment processing at both levels, configure local PostgreSQL, apply the accounting migrations, and match any arithmetic cutover exactly. Do not bypass the schema/privilege checks. |
| PPS balances increase but payments wait | Check minimum payment, spendable reserve, Oyster synchronization/unlock state, pending payout intent and quarantined blocks. Stop admission if reserve funding is inadequate; accepted liabilities remain owed. |
| Missing canonical block identity or payout synchronization fails | Check that RPC returns valid block hashes, not null, empty or malformed results. Restore node/wallet RPC health and retry; do not manually orphan rewards or resend payments to bypass this hold. |
| Payments held for an above-tip or deferred confirmed block | Restore node synchronization and let classification recheck the exact block hash on a stable chain. Catch-up alone does not clear the durable hold. Held blocks are prioritized across bounded batches, including after restart. Existing submitted intents can still reconcile; do not delete holds/intents or manually resend. |
| PPLNS rewards are smaller than a full block | Check `factor` and whether enough assigned work fills the window. Achieved proof difficulty does not determine payout weight. |
| PPLNS reports an invalid factor | Set a positive JSON number; zero, negative values, null and strings are rejected before mining starts. |
| Miner connects but no work arrives | Check the selected Pearl algorithm, supported release, TLS port, valid network-specific wallet, and `v2` protocol. Inspect template readiness. |
| Hashrate is zero | Check the miner's device/kernel diagnostics. BzMiner GPU kernel/profile incompatibility is not fixed by changing pool difficulty; use the certified path. |
| `Unsupported Pearl pool profile` | Use `m<=32772`, `n<=15360`, `k=32768`, rank 128, tile 8×16. Clear conflicting miner environment/config overrides and restart it. Other consensus-valid profiles are not admitted by this bounded pool implementation. |
| Few shares, no errors | Lower initial/minimum difficulty appropriately for that worker. Mainnet block discovery is probabilistic. Compare accepted work over a meaningful interval. |
| Stale or unknown job | Refresh work, check connectivity/template freshness, and submit through the same session. Do not replay work after reconnecting or a tip change. |
| Duplicate share | Stop replaying the same proof. Check the previous durable receipt; repeated acknowledgments must not create additional credit. |
| Proof service unavailable / verifier busy | Check worker exit status, memory headroom, verification concurrency, and queue depth. Service failures are not proof of miner misconduct. Restore service before increasing load. |
| Candidate queue keeps growing | Check certificate-generation failures, RPC availability, and node acceptance. Retain the ledger and diagnostics; do not delete durable candidate records to hide the fault. |
| Confirmed payout cannot be found by `getrawtransaction` | `txindex` may be disabled. Restore canonical `getblock` evidence and wallet history; the handler can reconcile without the index. Do not create another send. |
| Intent has no known transaction after a rejected send | Follow the approved no-broadcast audit above. RPC error text or an empty lookup alone cannot justify releasing it. |
| Gateway fails to start | Check private config/directory ownership and permissions. An existing socket is not overwritten: confirm no adapter is running before removing a stale socket. |
| Gateway TLS failure | Check DNS name, expiry, and trusted CA. Set `ca_file` for a private CA; do not disable verification. |
| Official client sees an RPC error | It is a final rejection or service failure, not acceptance. Refresh work and inspect private pool diagnostics. Never convert an error into `submitted`/success. |
| Reward not paid yet | Check canonical block status, confirmation/maturity threshold, wallet sync/unlock, minimum balance, both payment flags, and transaction status. Avoid blindly resending uncertain payouts. |

When reporting a problem, share versions, profile dimensions, aggregate test counts,
and sanitized error categories. Remove credentials, wallet secrets, real hostnames,
host addresses, user names, filesystem paths, and unrelated machine details from logs.
