# Scrypt coin-definition provenance

Miningcore's bundled `coins.json` contains daemon and proof-of-work contracts, not a promise that a
coin's network, exchange market, explorer or upstream project will remain available. Before opening
a production pool, build the linked daemon source, synchronize it, call `getblocktemplate` with the
same account used by Miningcore, and prove block submission on a private or test network where the
project provides one.

The following definitions were checked on 28 August 2026 against their maintained or canonical
daemon sources. Every entry uses Bitcoin-family `getblocktemplate`/`submitblock`, SHA-256d coinbase
and block identifiers, and Scrypt `N=1024, r=1` proof-of-work unless the notes say otherwise.

## Direct and hybrid templates

| Miningcore key | Symbol | Daemon source | Additional contract |
| --- | --- | --- | --- |
| `blockchaincoinx` | XCCX | [BlockChainCoinX](https://github.com/5erendipity/BlockChainCoinX) | Hybrid PoW/PoS block hashing |
| `catcoin` | CAT | [Catcoin Core](https://github.com/CatcoinCore/catcoincore) | MWEB code exists upstream, but its configured mainnet activation height has not been reached |
| `cyberyen` | CY | [Cyberyen](https://github.com/cyberyen/cyberyen) | MWEB mining rule and extension serialization |
| `ferrite` | FEC | [Ferrite Core](https://github.com/ferritecoin/ferritecoin) | MWEB mining rule and extension serialization |
| `ibithub` | IBH | [iBitHub](https://github.com/ibithub/ibithub) | Direct Scrypt template; the reviewed daemon does not implement an AuxPoW child chain |
| `litecoin-ii` | LC2 | [Litecoin II](https://github.com/litecoinII-project/litecoinII) | MWEB mining rule and extension serialization |
| `mateablecoin-scrypt` | MTBC | [MateableCoin](https://github.com/mateable/mateablecoin-24.x) | Hybrid chain; appends the daemon-required `"scrypt"` algorithm argument to `getblocktemplate` |
| `stohncoin` | SOH | [StohnCoin](https://github.com/StohnCoin-Projects/StohnCoin) | Direct Scrypt template |
| `theminerzcoin` | TMC | [TheMinerzCoin](https://github.com/MrMiner-org/TheMinerzCoin) | Hybrid PoW/PoS block hashing |
| `craftcoin` | CRC | [Craftcoin](https://github.com/craftcoin/craftcoin) | Direct Scrypt only; the reviewed canonical source does not implement AuxPoW |

## AuxPoW-capable child chains

| Miningcore key | Symbol | Daemon source | Payout-confirmation policy |
| --- | --- | --- | --- |
| `b1t` | B1T | [B1T](https://github.com/bittoshimoto/Bit) | 251 confirmations for the active 240-block maturity plus reorganization margin |
| `bells` | BEL | [Bells](https://github.com/Nintondo/bellscoinV3) | Default 102 confirmations exceed its 30-block maturity |
| `bonkcoin` | BONC | [BonkCoin](https://github.com/Bonkcoin/Bonkcoin-core) | 251 confirmations |
| `dingocoin` | DINGO | [Dingocoin](https://github.com/dingocoin/dingocoin) | 251 confirmations |
| `earthcoin` | EAC | [Earthcoin](https://github.com/Sandokaaan/Earthcoin) | Default 102 confirmations exceed its 30-block maturity |
| `flopcoin` | FLOP | [Flopcoin](https://github.com/Flopcoin/Flopcoin) | 251 confirmations |
| `junkcoin` | JKC | [Junkcoin](https://github.com/Junkcoin-Foundation/junkcoin-core) | Default 102 confirmations exceed its 70-block maturity |
| `luckycoin` | LKY | [Luckycoin](https://github.com/LuckycoinFoundation/luckycoin) | Default 102 confirmations exceed its 70-block maturity |
| `newyorkcoin` | NYC | [NewYorkCoin](https://github.com/jamesburrell2/newyorkcoin_v2) | Default 102 confirmations; configured MWEB activation remains ahead of the current chain |
| `pepecoin` | PEP | [Pepecoin](https://github.com/pepecoinppc/pepecoin) | 251 confirmations |
| `shibainucoin` | SHIC | [Shiba Inu Coin](https://github.com/shibacoinppc/shibacoin) | 251 confirmations |
| `trumpow` | TRMP | [Trumpow](https://github.com/trumpowppc/trumpow) | 251 confirmations |

An AuxPoW-capable coin definition does not by itself enable merged mining. Miningcore's integrated
coordinator currently implements the reviewed Litecoin-parent/Dogecoin-child SOLO topology. Treat
any other parent/child pairing as unsupported until its daemon RPC contract, chain identifiers,
coinbase commitment and submission path have dedicated implementation and daemon-backed tests.

## Why Quai Scrypt is not in `coins.json`

Quai's Scrypt lane is not a Bitcoin-family daemon with a conventional block template. The official
[SHA/Scrypt mining guide](https://docs.qu.ai/guides/miner/sha-scrypt-mining) and
[SOAP architecture](https://docs.qu.ai/learn/advanced-introduction/soap) use hierarchical
WorkObjects, workshares and Quai-specific mining RPC. The official
[go-quai-stratum](https://github.com/dominant-strategies/go-quai-stratum) service implements that
protocol boundary.

Adding `QUAI` to `coins.json` with an ordinary Scrypt hasher would therefore advertise a template
that cannot construct or submit valid Quai work. Quai support requires a dedicated Miningcore coin
family, job manager, serializer, submission path and daemon-backed vectors; it must not be
represented as a metadata-only coin addition.
