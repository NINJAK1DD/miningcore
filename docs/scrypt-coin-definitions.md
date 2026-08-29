# Scrypt coin-definition provenance

Miningcore's bundled `coins.json` contains daemon and proof-of-work contracts, not a promise that a
coin's network, market, explorer, or upstream project will remain available. Before opening a
production pool, build the reviewed daemon revision, synchronize it, call `getblocktemplate` with
the same account used by Miningcore, and prove block submission on a private or test network where
the project provides one.

The definitions below were checked on 28 August 2026 against the immutable daemon revisions linked
in the table. They use Bitcoin-family block-template work, SHA-256d coinbase identifiers, and Scrypt
`N=1024, r=1` proof-of-work. Submission uses the daemon-supported BIP22 path. XCCX and iBitHub are
marked as legacy daemons in the template because their reviewed RPC surfaces predate
`getblockchaininfo` and `getnetworkinfo`.

## Shipped definitions

| Miningcore key | Symbol | Reviewed daemon revision | Additional contract |
| --- | --- | --- | --- |
| `blockchaincoinx` | XCCX | [BlockChainCoinX `90563808`](https://github.com/5erendipity/BlockChainCoinX/tree/90563808fe3449e2581f6881b054d92a30ab8cd8) | Community-maintained hybrid chain; transactions carry `nTime`, blocks carry a signature trailer, and Scrypt identifies blocks. Its legacy `validateaddress` response omits the raw key, so the pool must configure `pubKey` explicitly |
| `catcoin` | CAT | [Catcoin Core `491a6ca1`](https://github.com/CatcoinCore/catcoincore/tree/491a6ca12bc301ec9aa5ab6fb3a4a120ae7d78de) | Requires the `mweb` client rule; serializes the extension when the daemon returns it |
| `cyberyen` | CY | [Cyberyen `cfd5045c`](https://github.com/cyberyen/cyberyen/tree/cfd5045ca723497e49de1100c47feecf724d8356) | Requires MWEB support; its consensus parameters continue to allow direct PoW after AuxPoW activation. Version rolling is disabled to preserve its strict chain ID |
| `ferrite` | FEC | [Ferrite Core `cfe3399f`](https://github.com/ferritecoin/ferritecoin/tree/cfe3399fa0cf8d61ec61fefe777283c93ce4931e) | MWEB-capable direct Scrypt template |
| `ibithub` | IBH | [iBitHub `add7989d`](https://github.com/ibithub/ibithub/tree/add7989deb53981da9d6601458b700fad562cce2) | Legacy Bitcoin RPC surface; direct Scrypt only |
| `litecoin-ii` | LC2 | [Litecoin II `47b7a37e`](https://github.com/litecoinII-project/litecoinII/tree/47b7a37eb8366019b43f028e91482d4fcc9a9ee2) | Requires the `mweb` client rule and template-driven extension serialization |
| `mateablecoin-scrypt` | MTBC | [MateableCoin `c5ae7b33`](https://github.com/mateable/mateablecoin-24.x/tree/c5ae7b3302c7b44d82dc8d69211f2877ff0feacf) | Passes the daemon-required `"scrypt"` algorithm argument; no unverified PoS block hasher is declared |
| `stohncoin` | SOH | [StohnCoin `87cb7eed`](https://github.com/StohnCoin-Projects/StohnCoin/tree/87cb7eed35560c894a6129c93e2e2853e454c8c9) | Direct Scrypt template |
| `theminerzcoin` | TMC | [TheMinerzCoin `026cf2f9`](https://github.com/MrMiner-org/TheMinerzCoin/tree/026cf2f9702c62c6685f3f7b5b7141f03a4536c7) | Hybrid serialization with transaction `nTime` and a block-signature trailer; current block identity is SHA-256d |
| `bells` | BEL | [Bells `92467bcd`](https://github.com/Nintondo/bellscoinV3/tree/92467bcd582aabade4f539c07dada4655d73bb18) | Community-maintained daemon; current consensus accepts direct non-AuxPoW blocks with the daemon-provided chain version. Version rolling is disabled to preserve its strict chain ID |
| `newyorkcoin` | NYC | [NewYorkCoin `36ba3dfe`](https://github.com/jamesburrell2/newyorkcoin_v2/tree/36ba3dfef8df702cee992bd142ca7881025cec8d) | Community fork whose source still labels the unit `LTC`; Miningcore uses the network-facing `NYC` symbol and advertises MWEB capability |

`hasMWEB` is a client-capability declaration, not an activation-height assertion. Capable daemons
require Miningcore to advertise both `segwit` and `mweb` when requesting work. Before activation the
template has no MWEB payload and Miningcore emits an ordinary block. When the daemon returns a
non-empty hexadecimal `mweb` field, Miningcore validates and appends that exact extension. Consensus
height and deployment state therefore remain owned by the daemon.

Dogecoin, Cyberyen, Bells, Namecoin and Lucky Bit reserve upper block-version bits for consensus
chain identifiers, so Miningcore declines Stratum `version-rolling` negotiation for them. PepePow
uses two overlapping bits for consensus algorithm selection and therefore exposes a reduced mask.
PACcoin also remains disabled because no authoritative daemon source could be established. The
[version-rolling audit](version-rolling.md) records the immutable revisions, guard placement and
per-template decisions.

## Definitions deliberately withheld

The following researched chains are not advertised because this review did not establish a complete,
daemon-backed direct-block submission contract or a Miningcore AuxPoW child-chain contract across
their activation states:

`b1t`, `bonkcoin`, `dingocoin`, `earthcoin`, `flopcoin`, `junkcoin`, `luckycoin`, `pepecoin`,
`shibainucoin`, and `trumpow`.

Some of these daemons may accept a direct non-AuxPoW block whose template version carries the chain
identifier. That possibility is not treated as proof of production support: each definition still
needs synchronized-daemon template, submission, acceptance, maturity and payout evidence. An
AuxPoW-capable coin definition also does not itself enable merged mining. Miningcore's integrated
coordinator currently implements the reviewed Litecoin-parent/Dogecoin-child SOLO topology. Other
child chains need explicit chain identifiers, coinbase commitments, serializers, submission RPCs,
and daemon-backed tests before they can be safely bundled. This work is tracked in
[issue #113](https://github.com/NINJAK1DD/miningcore/issues/113).

Craftcoin is also withheld. Its canonical daemon is unmaintained, lacks modern chain/network RPCs
and `submitblock`, and has no currently verifiable explorer or daemon-backed submission evidence.
Operators should not infer support from the existence of a generic Scrypt hasher.

## Why Quai Scrypt is not in `coins.json`

Quai's Scrypt lane is not a Bitcoin-family daemon with a conventional block template. The official
[SHA/Scrypt mining guide](https://docs.qu.ai/guides/miner/sha-scrypt-mining) and
[SOAP architecture](https://docs.qu.ai/learn/advanced-introduction/soap) use hierarchical
WorkObjects, workshares, and Quai-specific mining RPC. The official
[go-quai-stratum](https://github.com/dominant-strategies/go-quai-stratum) service implements that
protocol boundary.

Adding `QUAI` to `coins.json` with an ordinary Scrypt hasher would advertise a template that cannot
construct or submit valid Quai work. Quai support requires a dedicated Miningcore coin family, job
manager, serializer, submission path, and daemon-backed vectors; it must not be represented as a
metadata-only coin addition. That implementation is tracked in
[issue #111](https://github.com/NINJAK1DD/miningcore/issues/111).
