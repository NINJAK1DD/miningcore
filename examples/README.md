# Example configurations

Choose the smallest topology that matches the deployment, copy it outside the repository as
`config.json`, and replace every `CHANGE_ME` value. Miningcore accepts comments in JSON; strict JSON
editors may report those comments even though Miningcore accepts them. Per-coin comments stay
compact so these files remain scannable; use the fully annotated
[`config.example.json`](../config.example.json) for field-by-field guidance.

These files are reviewed operational baselines, not production secrets or promises that one setting
fits every miner fleet. Primary pool-wallet fields deliberately use `CHANGE_ME` placeholders so
copying an example cannot silently redirect block rewards. Every pool includes a
`rewardRecipients` entry demonstrating either a maintainer donation address listed in the main
README or an operator-supplied placeholder, but every sample percentage is `0`. Miningcore does not
require a reward recipient to mine or pay miners; set a reviewed non-zero percentage only when the
pool should collect a fee or donation.

Do not commit populated configurations. Miningcore does not have a parse-only startup flag; validate
on an isolated staging host or during a controlled maintenance window, then stop the foreground
process after its startup checks complete:

```console
./Miningcore -c /etc/miningcore/config.json
```

Follow the full [safe validation sequence](../docs/configuration.md#validate-changes-safely) before
moving production traffic.

All example APIs bind to loopback and assume a same-host HTTPS reverse proxy. Miningcore does not
process trusted forwarded-client headers, so it sees that proxy as the client and cannot apply a
meaningful per-public-client limit. Application rate limiting is therefore explicitly disabled in
these files; enforce request limits at the reverse proxy. Enable Miningcore's limiter only for a
topology where it observes each real client address directly.

Every enabled internal Stratum pool uses the integrated cluster ban manager and a pool threshold of
50 submitted shares, 50% invalid shares and a 600-second ban. It also explicitly disconnects miners
after 600 seconds without activity and checks idle VarDiff workers every 30 seconds. The examples
intentionally enable junk-request, invalid-share and invalid-login protection; review those values
for the expected miner fleet before deployment.

## Common pool layouts

| Example | Use it for |
| --- | --- |
| [`bitcoin_pool.json`](bitcoin_pool.json) | One Bitcoin SOLO pool with low- and high-difficulty Stratum ports |
| [`bitcoin_cash_pool.json`](bitcoin_cash_pool.json) | One Bitcoin Cash SOLO pool using CashAddr and low/high SHA-256 ports |
| [`bitcoin_bitcoin_cash_pool.json`](bitcoin_bitcoin_cash_pool.json) | Independent Bitcoin and Bitcoin Cash pools with separate daemon and Stratum ports |
| [`dogecoin_pool.json`](dogecoin_pool.json) | One direct Dogecoin SOLO pool with low- and high-difficulty ports |
| [`bitcoin_dogecoin_pool.json`](bitcoin_dogecoin_pool.json) | Independent Bitcoin and Dogecoin pools managed by one Miningcore process |
| [`litecoin_dogecoin_merged_mining_pool.json`](litecoin_dogecoin_merged_mining_pool.json) | Litecoin PPLNS parent with Dogecoin PROP AuxPoW accounting, demonstrating independent mixed schemes and two miner difficulty tiers |
| [`litecoin_pool.json`](litecoin_pool.json) | Legacy full Litecoin PPLNS example |

Direct Bitcoin-family examples remain `SOLO` by default. To commission PPS, start from the intended
coin's reviewed direct example, apply the required migrations, and change only its payout contract
after completing the [PPS operator guide](../docs/pps.md). There is intentionally no copy-first PPS
example that silently accepts an operator liability without the reserve and ledger checks.

Every example that opens an internal Stratum listener has named low- and high-difficulty tiers. A
few protocols retain a useful medium or optional-TLS tier as well. Receiver-only share recorders and
the auxiliary-only DOGE pool do not open miner listeners, so duplicating their dormant endpoint
metadata would be misleading.

The low/high labels are operational hints, not fixed hardware classes. The catalogue uses a
consistent relative spread on each coin family's existing Miningcore difficulty scale: low tiers
admit smaller or commissioning miners, while high tiers reduce share traffic from ASIC farms,
proxies and rental hashpower. These are reviewed commissioning baselines, not measurements of every
current miner model. Every active tier targets one accepted share per 15 seconds, retargets every 90
seconds and uses a 30% variance band. `maxDelta`, when present, is an absolute difficulty-step cap,
not a percentage; retune it together with the tier's scale. The examples also state the built-in
30-second idle VarDiff sweep explicitly so copied configurations retain an auditable value. Watch
actual accepted-share cadence and adjust deliberately; network difficulty, firmware and hashrate
can make any static starting value unsuitable. A compatible miner can request a starting difficulty
with `d=VALUE` in its password.

Polling and payout values remain coin-specific. A zero block-poll interval is intentional only where
the example supplies the protocol's subscription or ZMQ update path. Positive polling intervals are
milliseconds; job-rebroadcast values are seconds. The cluster payout manager runs every 600 seconds,
while each positive `minimumPayment` is denominated in that pool's coin. Recheck payout economics,
wallet fee policy, dust rules and daemon capabilities before accepting miners.

Dogecoin is not merge-mined with Bitcoin. The Bitcoin/Dogecoin example runs two independent pools.
Use the Litecoin/Dogecoin example for AuxPoW merged mining and read the
[merged-mining guide](../docs/merged-mining-litecoin-dogecoin.md) before enabling it.

Bitcoin and Bitcoin Cash are also independent in the combined example. Their normal mainnet RPC,
P2P and automatic-onion listeners overlap. The collision-free same-host baseline keeps Bitcoin Core
on its defaults. Start BCHN with `-datadir=/var/lib/bitcoin-cash` (or an equivalently isolated
`-conf` path) so it cannot read Bitcoin Core's configuration, then place these entries in BCHN's
separate `bitcoin.conf`:

```ini
datadir=/var/lib/bitcoin-cash
rpcport=8432
port=8433
listenonion=0
```

See BCHN's [configuration-file documentation](https://docs.bitcoincashnode.org/doc/bitcoin-conf/)
for `-datadir`, `-conf` and file-discovery behavior.

The Miningcore BCH daemon endpoint uses `8432`. If the BCH node must provide an inbound onion
service, replace `listenonion=0` with a separately reviewed non-conflicting onion bind and Tor target.
Bitcoin Cash pool and reward addresses use mainnet P2PKH CashAddr values beginning with `q`, with the
canonical explicit `bitcoincash:` prefix used consistently across the examples and startup banner.
P2SH CashAddr values beginning with `p` are not supported by the current payout path, and
`addressType` remains `BCash`.

## Additional coin examples

These examples retain their coin-specific daemon and payout fields while following the same
listener, VarDiff, timeout, banning, credential and payout-safety contracts as the common layouts.
Their defaults are practical commissioning baselines, but network liveness and third-party daemon
compatibility can change independently of a Miningcore release.

| Coin or mode | Example |
| --- | --- |
| Alephium | [`alephium_pool.json`](alephium_pool.json) |
| Beam | [`beam_pool.json`](beam_pool.json) |
| Callisto | [`callisto_pool.json`](callisto_pool.json) |
| Conceal | [`conceal_pool.json`](conceal_pool.json) |
| Cortex | [`cortex_pool.json`](cortex_pool.json) |
| Dash | [`dash_pool.json`](dash_pool.json) |
| Dash without polling | [`dash_pool_no_polling.json`](dash_pool_no_polling.json) |
| DigiByte Scrypt | [`digibyte_scrypt_pool.json`](digibyte_scrypt_pool.json) |
| DigiByte SHA-256 | [`digibyte_sha256_pool.json`](digibyte_sha256_pool.json) |
| DigiByte Odocrypt | [`digibyte_odocrypt_pool.json`](digibyte_odocrypt_pool.json) |
| Ethereum | [`ethereum_pool.json`](ethereum_pool.json) |
| Ethereum Classic | [`ethereumclassic_pool.json`](ethereumclassic_pool.json) |
| Firo | [`firo_pool.json`](firo_pool.json) |
| FLO | [`flo_pool.json`](flo_pool.json) |
| Handshake | [`handshake_pool.json`](handshake_pool.json) |
| Kaspa | [`kaspa_pool.json`](kaspa_pool.json) |
| Litecoin/Dash | [`litecoin_dash_pool.json`](litecoin_dash_pool.json) |
| Monero | [`monero_pool.json`](monero_pool.json) |
| Nexa | [`nexa_pool.json`](nexa_pool.json) |
| OctaSpace | [`octaspace_pool.json`](octaspace_pool.json) |
| Pakcoin | [`pakcoin_pool.json`](pakcoin_pool.json) |
| Ravencoin | [`ravencoin_pool.json`](ravencoin_pool.json) |
| SatoshiCash | [`satoshicash_pool.json`](satoshicash_pool.json) |
| Ubiq | [`ubiq_pool.json`](ubiq_pool.json) |
| Verus Coin | [`veruscoin_pool.json`](veruscoin_pool.json) |
| Warthog | [`warthog_pool.json`](warthog_pool.json) |
| Xelis | [`xelis_pool.json`](xelis_pool.json) |
| Zano | [`zano_pool.json`](zano_pool.json) |
| Zcash | [`zcash_pool.json`](zcash_pool.json) |
| Zephyr | [`zephyr_pool.json`](zephyr_pool.json) |

## Distributed recorder layout

| Example | Role |
| --- | --- |
| [`bitcoin_share_relay_sender.json`](bitcoin_share_relay_sender.json) | Edge Stratum node that relays ordinary Bitcoin shares without owning payouts |
| [`bitcoin_share_relay_recorder.json`](bitcoin_share_relay_recorder.json) | Central PostgreSQL recorder and sole payout owner for two example senders |

The two relay files form one example deployment and must use the same stable pool ID and coin
definition. Give every process a unique `instanceId`, generate a different long secret for each
sender/receiver relationship, and expose relay ports only on a private network or VPN. The addresses
in these files use the documentation-only `192.0.2.0/24` range and must be replaced.

Ordinary share relay uses ZeroMQ PUB/SUB and is not a durable acknowledged queue. Shares sent while
the recorder or route is unavailable are not replayed. Only the recorder should enable payment
processing for this pool/database set. Read the [share-relay guide](../docs/share-relays.md) before
using this advanced topology.

## Production checklist

- Replace every `CHANGE_ME` value and every documentation-only address.
- Review every zero-percent `rewardRecipients` entry and set a fee only deliberately.
- Use unique pool IDs and do not rename an ID after it has accounting history.
- Keep daemon RPC, PostgreSQL, admin, metrics and relay listeners behind appropriate firewalls.
- Configure public-client request limiting on the HTTPS reverse proxy used by these examples.
- Put API and Stratum TLS keys outside source control and restrict their filesystem permissions.
- Create PostgreSQL schema/migrations and exact share partitions before enabling a pool.
- Fund Bitcoin-family payout wallets with a confirmed fee reserve before enabling payments.
- Keep only one payout/reconciliation owner for each database and pool set.
- Back up wallet and database state, then test restoration away from production.
- Read [Configuration](../docs/configuration.md), [Operations](../docs/operations.md), and
  [Troubleshooting](../docs/troubleshooting.md) before admitting miners.

All JSON examples are parsed and passed through Miningcore's normal-startup configuration validator
in CI. That proves their structure and cross-setting contracts; it cannot verify operator-supplied
wallet addresses, credentials, daemon availability, network interfaces or firewall policy. CI also
derives each coin family's exact pool, daemon and payment extension contracts. It rejects unknown,
mis-cased, wrong-family, wrong-scope and wrong-typed extension values before they can disappear
silently into extension data.
