# DigiByte direct mining

Miningcore provides separate direct-mining templates for DigiByte SHA-256d, Scrypt, Skein, Qubit
and Odocrypt. Select exactly one algorithm per pool and configure the connected DigiByte daemon for
the same algorithm. These are direct-mining paths; DigiByte is not presented as an auxiliary chain.

## Support boundary

| Capability | Current status |
| --- | --- |
| Template present | All five active mainnet algorithms |
| Source reviewed | DigiByte Core v9.26.5 at commit `05b50e229db5a3d1fb316c77f3f6c62efa879b96` |
| Odocrypt native implementation | Pinned upstream cipher, activation- and schedule-aware wrapper and Windows/Linux known-answer tests |
| Daemon-backed boundary | DigiByte Core v9.26.5 regtest activation, daemon `odokey`, Stratum work, accepted share and daemon-accepted block verified with the pinned official miner revision |
| Direct Stratum mining | Implemented through the Bitcoin-family runtime |
| Merged mining | Not implemented or advertised |
| Retired Myriad-Groestl | Deliberately absent from current-mainnet DigiByte templates |
| Packaged platforms | Ubuntu Linux x64 and Windows x64; macOS and Alpine/musl CI cover the managed listener only and are not full Odocrypt runtime claims |

The presence of a template is not a guarantee that an arbitrary daemon or miner version is
compatible. Use the reviewed daemon release, stage the pool privately, and complete the operational
checks below before accepting public miners.

The daemon-backed verification crossed activation at regtest height 600 and submitted an Odocrypt
block at height 601. It does not replace the remaining deployment checks for PostgreSQL persistence,
stale/invalid-share rejection, reconnect behavior, maturity, payouts or a sustained public-network
soak.

## Reviewed consensus contract

The implementation is based on immutable DigiByte sources:

- [DigiByte Core v9.26.5](https://github.com/DigiByte-Core/digibyte/releases/tag/v9.26.5),
  commit [`05b50e2`](https://github.com/DigiByte-Core/digibyte/commit/05b50e229db5a3d1fb316c77f3f6c62efa879b96).
- [`OdoKey` and header hashing](https://github.com/DigiByte-Core/digibyte/blob/05b50e229db5a3d1fb316c77f3f6c62efa879b96/src/primitives/block.cpp)
  derive the cipher key as `nTime - (nTime % nOdoShapechangeInterval)` and hash the complete
  serialized 80-byte header.
- [`chainparams.cpp`](https://github.com/DigiByte-Core/digibyte/blob/05b50e229db5a3d1fb316c77f3f6c62efa879b96/src/kernel/chainparams.cpp)
  sets the Odocrypt activation heights to `9,112,320` (mainnet), `500` (testnet), and `600`
  (signet and regtest). It sets the mainnet and regtest shape-change schedule to ten days and the
  testnet and signet schedule to one day. Miningcore treats both values as typed network contracts,
  refuses a pre-activation template, derives the key from the template time, and requires it to
  equal the daemon's `odokey` response before hashing a share.
- The vendored [`odocrypt.cpp`](https://github.com/DigiByte-Core/digibyte/blob/05b50e229db5a3d1fb316c77f3f6c62efa879b96/src/crypto/odocrypt.cpp)
  and [`odocrypt.h`](https://github.com/DigiByte-Core/digibyte/blob/05b50e229db5a3d1fb316c77f3f6c62efa879b96/src/crypto/odocrypt.h)
  are byte-for-byte copies of that revision. Miningcore's small `hashodo.h` adapter replaces the
  daemon's `uint256` return type without changing the cipher-plus-Keccak operation.
- The Keccak-p[800] reference implementation and header used by the adapter are included in the
  same build-time SHA-256 manifest, so any change to the complete native input set fails before
  compilation.
- Official miner revision
  [`91297fd`](https://github.com/DigiByte-Core/dgbminer/tree/91297fdfc42284c743d8a4d174973b50ec5e73d2)
  [names the algorithm `odo`](https://github.com/DigiByte-Core/dgbminer/blob/91297fdfc42284c743d8a4d174973b50ec5e73d2/algo-gate-api.c#L248),
  [uses target factor `1`](https://github.com/DigiByte-Core/dgbminer/blob/91297fdfc42284c743d8a4d174973b50ec5e73d2/cpu-miner.c#L133),
  and documents mainnet RPC port `14022`.

Current DigiByte mainnet accepts SHA-256d, Scrypt, Skein, Qubit and Odocrypt. Odocrypt replaced
Myriad-Groestl; current AlgoLock rules reject the retired algorithm. Do not restore a
`digibyte-groestl` template or point an old Groestl configuration at current mainnet.

Odocrypt uses the standard Stratum target domain. The pinned official miner leaves its target
factor at `1`; Miningcore therefore does not inherit the retired Groestl template's `256` share
multiplier. Scrypt remains the separate DigiByte algorithm that uses `65,536` scaling.

## Daemon baseline

Use a dedicated, fully synchronized DigiByte Core v9.26.5-or-newer node. Keep RPC bound to a private
interface and restrict it to the Miningcore host. A minimal mainnet fragment is:

```ini
server=1
algo=odo
rpcbind=127.0.0.1
rpcallowip=127.0.0.1
rpcport=14022
rpcuser=CHANGE_ME_DAEMON_RPC_USER
rpcpassword=CHANGE_ME_DAEMON_RPC_PASSWORD
```

Use a strong unique RPC password and never expose the RPC port to miners or the public Internet.
For another DigiByte algorithm, change `algo` and use its matching Miningcore template. Do not share
one algorithm-locked daemon endpoint among differently configured DigiByte pools.

Start with [`digibyte_odocrypt_pool.json`](../examples/digibyte_odocrypt_pool.json). Replace every
`CHANGE_ME` value, choose unused Stratum/API ports, and validate the pool wallet on the same network.
The example's low/high difficulties are commissioning baselines; tune them from measured miner
share times rather than treating them as guarantees for every FPGA or miner implementation.

## Payout and maturity notes

Mainnet uses P2PKH prefix `30`, script prefix `63`, and Bech32 HRP `dgb`. Miningcore asks the daemon
to validate the configured address, so a testnet or malformed address must fail startup. At current
heights coinbase outputs require 100 confirmations; keep the daemon synchronized and do not lower
the payout handler's confirmation requirement below consensus maturity.

Automated payouts require the normal Bitcoin-family wallet, unlock, reserve, backup and reorg
controls described in the [operator handbook](operations.md) and
[troubleshooting guide](troubleshooting.md). Commission with `SOLO` or disabled payment processing
until job acquisition and share persistence have been observed end to end.

## Commissioning checks

Before opening the pool:

1. Confirm `getblockchaininfo` reports current headers and blocks with no initial block download.
2. Call `getblocktemplate` with the daemon's `odo` selection and confirm the returned version selects
   Odocrypt.
3. Start Miningcore privately and require a clean daemon health check and a new job.
4. Connect a compatible Odocrypt miner; verify subscribe, authorize, difficulty, job and one accepted
   share, then confirm the share reaches PostgreSQL.
5. Submit an intentionally stale and an invalid share and require rejection without recorder failure.
6. Exercise a clean Miningcore stop/start and a daemon disconnect/reconnect before enabling payouts.
7. Preserve the configuration, exact daemon/miner versions and resulting logs as the deployment's
   certification record.

## Rollout and rollback

There is no database migration. The catalogue change intentionally removes the obsolete
`digibyte-groestl` identifier rather than silently redirecting it to another proof of work. Stop the
affected pool before upgrading, retain its previous configuration, then stage
`digibyte-odocrypt` with a dedicated `algo=odo` daemon and Stratum port.

If commissioning fails, stop that pool and preserve its daemon, Miningcore and miner logs. Roll
back to a previously working Miningcore build only with one of the other active DigiByte algorithms
and its matching daemon/miner configuration. A previous build's Groestl template is not a safe
current-mainnet fallback. Do not enable payouts or reopen the pool until accepted-share and block
submission behavior has been re-established.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `Invalid coin-template ... odocrypt` at startup | Do not override the built-in activation or schedule values. Custom Odocrypt templates require canonical nonzero activation heights and intervals for main, test, signet and regtest. |
| Daemon rejects the GBT algorithm | Confirm DigiByte Core v9.26.5 or newer, `algo=odo`, and that the endpoint is the intended DigiByte node. |
| Every submitted share is invalid | Confirm the miner is actually using Odocrypt, the Stratum port belongs to the Odocrypt pool, and system clocks are sane. |
| Blocks are rejected although shares pass | Stop public mining; preserve the job/header/submission logs and verify daemon version, algorithm selector, schedule and block identity before resuming. |
| Old configuration references `digibyte-groestl` | Replace it with a supported active algorithm. There is no compatibility alias because silently mining a retired consensus algorithm is unsafe. |

For generic daemon, Stratum, persistence and payout failures, continue with
[Troubleshooting](troubleshooting.md).
