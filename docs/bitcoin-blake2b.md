# Bitcoin BLAKE2b header-v2 mining

This is support for a **separate hard-fork chain**, not a new algorithm selectable on
SHA-256d Bitcoin. Use `coin: "bitcoin-blake2b"`; the existing `bitcoin` template and its
direct-coinbase SOLO policy are unchanged. Miningcore labels this chain `BTCB2B` to keep
pool/accounting identities distinct; that label is not a claim about an exchange ticker.

[BIP-110](https://github.com/bitcoin/bips/blob/master/bip-0110.mediawiki) describes the
Reduced Data Temporary Softfork. It is not the BLAKE2b proof-of-work specification. The
later hard fork is implemented by the pinned Knots sources listed below.

## Compatibility boundary

- A Miningcore build containing this feature is required; v0.3.0 does not contain it.
- The reviewed node is **Bitcoin Knots v29.4.1.knots20260508**, commit
  `8c85b1585dac23f964e2dd32045624de7f02aa58`. Startup requires its version and Knots identifier,
  an active deployment with the expected activation height, and mandatory GBT rule `!blake2b`.
  Version strings are compatibility checks, not proof of binary authenticity: independently
  verify the upstream release checksums and signatures.
- Mainnet first uses header-v2 at height **961640**. Its activation coinbase headline is
  `8-30 NYPost Deride And Conquer` and the one-time target shift is 22. Miningcore validates
  that first target against the parent and the mainnet proof-of-work limit; it does not
  apply another shift to ordinary shares or subsequent daemon targets.
- Only mainnet and isolated regtest are configured. Testnet4 and signet are not advertised.
  The regtest fixture uses activation 20 and shift 20; its headline is
  `Miningcore BLAKE2b regtest`. These are a test contract, not mainnet settings.
- The node release is stable. The project's compatible DATUM gateway/miner ecosystem is
  still described as public beta. A stable node does not prove compatibility with every
  ASIC, firmware, proxy, rental service or public network deployment.

## Node and wallet isolation

Use a separate daemon data directory, wallet, RPC credentials, pool ID and ledger attribution.
Do not repoint your SHA-256d Bitcoin pool or use a copied production wallet as a shortcut.
The chains share historical addresses and transactions; assess replay and wallet risks
independently before funding reserves or making payments. Miningcore does not add replay
protection to transactions created by a daemon wallet.

Run the verified node in that dedicated directory. For a same-host deployment alongside
ordinary Bitcoin, the example deliberately uses non-default ports. Confirm these are unused
by every local daemon, testnet and service first:

```ini
server=1
rpcbind=127.0.0.1
rpcallowip=127.0.0.1
rpcport=18332
rpcuser=CHANGE_ME_BLAKE2B_RPC_USER
rpcpassword=CHANGE_ME_BLAKE2B_RPC_PASSWORD
port=18333
bind=0.0.0.0:18333
bind=127.0.0.1:18334=onion
```

This remains **mainnet**: do not add `testnet`, `testnet4`, `signet` or `regtest` to a
mainnet deployment. Use the dedicated `-datadir` with every `bitcoin-cli` command too.
Let the daemon synchronize, load its dedicated payout wallet and inspect:

```console
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getnetworkinfo
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getdeploymentinfo
bitcoin-cli -datadir=/path/to/dedicated-blake2b-node getblocktemplate '{"rules":["segwit","blake2b"]}'
```

`getdeploymentinfo.blake2b` must have the expected height and `active: true`; the template
must advertise `!blake2b`. Stable Knots does **not** promise a `coinbaseaux.blake2b_headline`
field. Miningcore uses reviewed typed activation metadata, not an assumed optional GBT field.
The pool wallet address must belong to the loaded wallet if the pool is to pay rewards.
Use the daemon endpoint's `httpPath` to select a wallet when more than one is loaded.

## Configure Miningcore

Start from [bitcoin_blake2b_pool.json](../examples/bitcoin_blake2b_pool.json). Keep this
configuration outside the checkout, replace every `CHANGE_ME` value, and follow the
[operator preflight](operations.md#before-accepting-miners). Review the RPC ports, pool-wallet
and fee addresses, PostgreSQL credentials, logging and recovery paths before opening Stratum.

The sample uses **custodial SOLO**: the chain wallet receives the block reward, then
Miningcore pays the winning miner after maturity, minus reviewed fees. It does not use
canonical Bitcoin's default direct-coinbase SOLO mode. Do not add `soloCoinbasePayout` or
`bip54Coinbase`, even as false: those settings belong to a different reviewed runtime.

SOLO, PROP and PPLNS use their existing payout/accounting paths. PPS uses immutable assigned
difficulty evidence and the existing transactional credit ledger; follow the complete
[PPS reserve, schema and recovery checklist](pps.md) before selecting it. Keep both pool
and cluster payment processing enabled for PPS. No new database schema is introduced.
Relay receivers must understand the new family and have the same template/chain contract;
do not introduce a new chain into a mixed-version accounting deployment.

Keep the fee entry at zero until its address and intended percentage are reviewed.
Do not infer current profitability, market value or reserve adequacy from hash-rate telemetry.

## Miner protocol and difficulty

Connect compatible BLAKE2b/Sia-style miners directly to Miningcore, using a valid address
on this chain as the username (`ADDRESS.worker`). A DATUM gateway is not required between
the miner and Miningcore; the separate DATUM pooled-mining protocol is not implemented.
SHA-256d hardware, including SHA-256 Bitaxe devices, cannot mine this chain.

The [upstream miner guide](https://btc-blake2b.org/miners) lists Antminer A3 and Sia-style
Goldshell devices. That is upstream compatibility information, **not** a Miningcore firmware
certification. No physical BLAKE2b ASIC/firmware is claimed tested by this implementation.
Commission each miner/proxy on an isolated endpoint before sending production hash power.

The production wire contract is Sia-style **profile 0**, with hasher time rolling disabled:

- Subscribe returns a four-byte connection extranonce and an eight-byte extranonce2 size.
- Notify contains the hidden previous hash, a 39-byte commitment in `coinb1`, empty `coinb2`,
  an empty merkle list, an eight-digit compact **share target**, and 16-digit miner time.
- Submit requires exactly five JSON strings: worker, job ID, extranonce2, time and nonce.
  Extranonce2, time and nonce must each contain exactly 16 hexadecimal characters.
  Extra version bits and shortened legacy fields are rejected, not padded or coerced.
- Version rolling is disabled; miners cannot change consensus-owned header fields. The
  miner-time bytes are nonce space for this fixed-time profile, not permission to change
  the committed consensus timestamp.
- Difficulty uses Bitcoin's `0x1d00ffff` reference target and multiplier 1, as in the
  reviewed gateway accounting contract. This is distinct from a miner display's SI units.
  Each assigned target is converted with exact integer arithmetic and truncated to the
  compact value actually sent on the wire. A valid network candidate is never discarded
  solely because its assigned share target is harder than the network target.
- Each job keeps its assigned difficulty snapshot across VarDiff changes. Changed targets
  require fresh notify data as well as `mining.set_difficulty`.
- Connection suffixes never wrap within a running allocator. A random 128-bit coinbase
  discriminator separates job commitments across processes and restarts. Duplicate work
  remains duplicate even if hexadecimal casing or the assigned target changes.

All four ASIC layouts and XOR-mask variants are covered by consensus primitives, but this
does not advertise selectable wire profiles 1–3 or anti-withholding service. Production
uses a zero XOR key. There is no user-supplied header-flags or profile override.

## Troubleshooting and validation limits

- **Startup refuses a node:** check exact version, RPC authentication, selected chain,
  deployment state, and `!blake2b`. Do not remove the gate or substitute the `bitcoin` template.
- **Malformed or unknown work:** check firmware/proxy field lengths and job preservation.
  Ordinary Bitcoin Stratum translation is not a compatible substitute.
- **Unexpected low-difficulty shares:** verify the notify compact target and miner protocol,
  not only the displayed `set_difficulty`. Avoid unreviewed time/version rolling.
- **Daemon rejects a candidate:** preserve the submission hash, daemon rejection reason,
  template and recovery evidence. Independently look up the block. A missing response is
  not acceptance, and `duplicate-invalid` must never be treated as success.
- **Accounting pipeline stops:** follow [recovery guidance](troubleshooting.md); never import
  a quarantine file as a recovery journal. Preserve PostgreSQL and all journals first.

Automated tests include the five official header-v2 vectors, strict configuration and share
parsing, exact target boundaries, difficulty snapshots, and a pinned-node activation/submission
fixture. The node fixture is enabled by `MININGCORE_TEST_BLAKE2B_BITCOIND` and is explicitly
skipped if that binary is unavailable; the main CI lane installs its checksum-pinned binary.
It drives real manager startup and newline-delimited TCP subscribe/authorize/configure/submit,
constructs Sia-style proofs independently from notify data, and verifies accepted blocks for
SOLO/PPS/PROP/PPLNS. It also checks VarDiff notification ordering, strict JSON rejection,
PPS admission evidence, coinbase maturity, fee allocation, and a confirmed wallet payment.
Its persistence sink is substituted: this test is not a PostgreSQL ledger integration test.
A second, explicitly gated test requires both that binary and `MININGCORE_TEST_POSTGRES`.
It feeds the real miner's accepted proof into the PostgreSQL accounting repository, verifies
PPS credit/remainder precision, duplicate replay and conflicting-payload rejection, and runs
SOLO/PROP/PPLNS allocation against actual share/balance tables. Confirmed or orphaned PPS
blocks cannot credit the same liability again or reverse it. Each run owns a disposable schema.
Inspect actual test results before deployment—test code existing is not evidence that a run passed.
Real-network maturity, payout liquidity, firmware behavior and long-running VarDiff require
operator commissioning beyond isolated regtest.

## Immutable source provenance

Protocol baseline rechecked against upstream on 2026-09-04 and 2026-09-05:

| Contract | Reviewed source |
| --- | --- |
| Stable node and release | [Knots v29.4.1.knots20260508](https://github.com/bitcoinknots/bitcoin/tree/8c85b1585dac23f964e2dd32045624de7f02aa58) |
| Header layout | [src/primitives/block.h](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/primitives/block.h) |
| H1/H2, ASIC profiles, PoW, XOR | [src/primitives/block.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/primitives/block.cpp) |
| Official vectors | [block_header_v2.json](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/test/data/block_header_v2.json) |
| GBT rules and version | [src/rpc/mining.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/rpc/mining.cpp) |
| Activation parameters and target shift | [chainparams.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/kernel/chainparams.cpp), [pow.cpp](https://github.com/bitcoinknots/bitcoin/blob/8c85b1585dac23f964e2dd32045624de7f02aa58/src/pow.cpp) |
| Miner work and target accounting | [CONVOY datum_pow.c](https://github.com/CONVOYMining/datum_gateway/blob/b9ea7dc3eb91352565ab487ec55ed6ee5964a440/src/datum_pow.c) |
| Miner notify, submit and payout coinbase selection | [CONVOY datum_stratum.c](https://github.com/CONVOYMining/datum_gateway/blob/b9ea7dc3eb91352565ab487ec55ed6ee5964a440/src/datum_stratum.c) |

The Knots and CONVOY default heads were unchanged from these pins at the recheck. New,
unmerged gateway proposals addressed strict parsing, duplicate replies, diagnostics and C
memory safety; they do not redefine this consensus baseline. Re-audit upstream before merge
and before accepting a new daemon revision rather than automatically tracking a moving branch.
