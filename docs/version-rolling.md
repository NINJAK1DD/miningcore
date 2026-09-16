# Bitcoin-family version rolling

Miningcore negotiates BIP310 version rolling against a pool-owned boundary. The miner may request
fewer bits, but it cannot expand that boundary. A request with no bits in common is declined rather
than returning a misleading zero mask.

The compatibility default for Bitcoin-family templates remains `0x1fffe000`. Its use without any
of the fields below is not a claim that the template has received a source audit, and an active pool
using that fallback logs a startup warning. Templates whose daemons use overlapping version bits
either declare a smaller source-reviewed mask or set `disableVersionRolling: true`. The standard
AuxPoW flag, bit 8 (`0x00000100`), is outside the default mask but is still recorded for strict
AuxPoW templates because it is consensus-owned.

## Template fields

```json
{
  "versionRollingMask": "0x1fff2000",
  "versionRollingConsensusMask": "0x0000c000"
}
```

- `versionRollingMask` is the nonzero set of bits miners may change. Omit it only when the standard
  Bitcoin-family mask is appropriate or rolling is disabled.
- `versionRollingConsensusMask` records consensus-owned bits found during the source audit. It is
  documentation and an enforced non-overlap boundary, not a second miner mask.
- `disableVersionRolling: true` declines the capability and preserves the daemon version exactly.

Template masks must be strings containing lowercase `0x` followed by exactly eight
case-insensitive hexadecimal digits.
Miningcore rejects zero masks, bits outside its BIP310 envelope, an allowed/consensus overlap, and
contradictory disabled-plus-allowed settings while loading templates, before Stratum listeners open.
Do not copy a mask between coins merely because they share a hasher or an AuxPoW ancestor.

## Source audit

The following possible chain-ID or consensus-bit candidates were reviewed on 29 August 2026.
“Before direct branch” means the strict check can reject ordinary blocks and rolling is disabled.
“AuxPoW branch only” means ordinary non-AuxPoW headers return before the chain-ID check.

| Template | Immutable daemon revision | Ordinary-header validation result | Miningcore policy |
| --- | --- | --- | --- |
| Dogecoin | [Dogecoin `cf0888a5`](https://github.com/dogecoin/dogecoin/blob/cf0888a55bc685d03e2b90e5659a5c119d1d7ee5/src/dogecoin.cpp#L90-L103) | `src/dogecoin.cpp`: strict chain-ID guard precedes the direct/AuxPoW branch | Disabled; `0xffff0100` recorded as consensus-owned |
| Cyberyen | [Cyberyen `cfd5045c`](https://github.com/cyberyen/cyberyen/tree/cfd5045ca723497e49de1100c47feecf724d8356) | `src/pow.cpp`: strict chain-ID guard precedes the direct/AuxPoW branch | Disabled; `0xffff0100` consensus-owned |
| Bells | [Bells `92467bcd`](https://github.com/Nintondo/bellscoinV3/tree/92467bcd582aabade4f539c07dada4655d73bb18) | `src/validation.cpp`: nonlegacy headers are chain-ID checked before direct acceptance | Disabled; `0xffff0100` consensus-owned |
| Namecoin | [Namecoin `ea20bb9d`](https://github.com/namecoin/namecoin-core/blob/ea20bb9d571d3a0a32effcd85065efaa076d212b/src/validation.cpp#L1883-L1897) | `src/validation.cpp`: chain-ID rejection precedes the AuxPoW branch | Disabled; `0xffff0100` consensus-owned |
| Lucky Bit | [Luckycoin `8b1fa2b5`](https://github.com/LuckycoinFoundation/luckycoin/blob/8b1fa2b541d1fb2e6d27ed67155ad0997ef4c99c/src/luckycoin.cpp#L92-L113) | `src/luckycoin.cpp`: strict chain-ID guard precedes the direct/AuxPoW branch | Disabled; `0xffff0100` consensus-owned |
| SkyDoge | [SkyDoge `46fc4b0a`](https://github.com/skydogenet/mainchain/tree/46fc4b0ac9ea3836c779c16a62ff2d365dd5f733) | No AuxPoW or chain-ID guard in the ordinary proof path | Standard `0x1fffe000` mask |
| PepePow | [PepePow `5a9debca`](https://github.com/MattF42/PePe-core/tree/5a9debcab3b014a182e24316864d0a95bc06f129) | `src/validation.cpp`: version bits `0x00004000` and `0x00008000` select/enforce consensus algorithms | Reduced `0x1fff2000`; `0x0000c000` consensus-owned |
| Verge (five algorithms) | [Verge `52b67120`](https://github.com/vergecurrency/VERGE/blob/52b67120b953aa4ef2dc897405ddd240e90bb5dd/src/primitives/block.h#L37-L44) | `BLOCK_VERSION_ALGO` owns `0x00007800`, including two bits in the default mask | Reduced `0x1fff8000`; `0x00007800` consensus-owned |
| ButKoin (two algorithms) | [ButKoin `875c6cb1`](https://github.com/ButKoin/but/blob/875c6cb1c353e15593fa46fc94837d2155010e75/src/primitives/block.h#L32-L40) | `BLOCK_VERSION_ALGO` owns `0x00007800`, including two bits in the default mask | Reduced `0x1fff8000`; `0x00007800` consensus-owned |
| Veles (two algorithms) | [Veles `c2215686`](https://github.com/velescore/veles/blob/c2215686c9f5b4c4734d4d8162a9b732bd9a0799/src/primitives/block.cpp#L33-L57) | `ALGO_VERSION_MASK` owns `0x0000ff00`; `GetPoWHash` dispatches from those bits | Reduced `0x1fff0000`; `0x0000ff00` consensus-owned |
| Litecoin Cash (two algorithms) | [Litecoin Cash `cf67257a`](https://github.com/litecoincash-project/litecoincash/blob/cf67257a008252ed283de4538bb5e4582036aaab/src/primitives/block.cpp#L45-L68) | `GetPoWType` reads bits 16–23 and selects SHA-256d or MinotaurX | Reduced `0x1f00e000`; `0x00ff0000` consensus-owned |
| Maza (two algorithms) | [Maza `326935b8`](https://github.com/MazaCoin/maza/blob/326935b8e0cbb7e1ed8a8affe04d9e40bad232d0/src/primitives/block.cpp#L45-L60) | `GetPoWType` reads bits 16–23 and selects SHA-256d or MinotaurX | Reduced `0x1f00e000`; `0x00ff0000` consensus-owned |
| PlexHive (two algorithms) | [PlexHive `42f8a40e`](https://github.com/PlexHive/PlexHive/blob/42f8a40e6830ac166d2ca66cec16c84623f50663/src/primitives/block.cpp#L45-L62) | `GetPoWType` reads bits 16–23 and selects SHA-256d or MinotaurX | Reduced `0x1f00e000`; `0x00ff0000` consensus-owned |
| DigiByte (five active algorithms) | [DigiByte v9.26.5 `05b50e2`](https://github.com/DigiByte-Core/digibyte/blob/05b50e229db5a3d1fb316c77f3f6c62efa879b96/src/primitives/block.h#L24-L42) | `BLOCK_VERSION_ALGO` owns `0x00000f00`, entirely below the pool mask; Odocrypt replaced Myriad-Groestl and AlgoLock rejects the retired selector | Standard `0x1fffe000`; `0x00000f00` consensus-owned |
| Auroracoin (five algorithms) | [Auroracoin `150c14c8`](https://github.com/aurarad/auroracoin/blob/150c14c8975ff4110ad7aace214c7d1bd26ab540/src/primitives/block.h#L32-L38) | `BLOCK_VERSION_ALGO` owns `0x00000f00`, entirely below the pool mask | Standard `0x1fffe000`; `0x00000f00` consensus-owned |
| Smileycoin (five algorithms) | [Smileycoin `204badcf`](https://github.com/smileycoin/smileyCoin/blob/204badcf54c9e2329b38fbf38d97d9803bb2fdfc/src/core.h#L33-L39) | `BLOCK_VERSION_ALGO` owns `0x00000e00`, entirely below the pool mask | Standard `0x1fffe000`; `0x00000e00` consensus-owned |
| Pyrk (three algorithms) | [Pyrk `c8a9d9d0`](https://github.com/pyrkcommunity/pyrk/blob/c8a9d9d01a03c5c16cb281461fa752fea420ec3a/src/primitives/block.h#L27-L38) | `BLOCK_VERSION_ALGO` owns `0x00000e00`, entirely below the pool mask | Standard `0x1fffe000`; `0x00000e00` consensus-owned |
| Mooncoin | [Mooncoin `9d7af8a1`](https://github.com/MooncoinCommunity/wallet/tree/9d7af8a1667bebbca2ee84386bd912807ef04e97) | No AuxPoW or chain-ID guard in the ordinary proof path | Standard `0x1fffe000` mask |
| DaneCoin | [DaneCoin `73d21d33`](https://github.com/danecoin/Danecoin/tree/73d21d335c11a8966c995b7e8c520c2b55695c04) | No AuxPoW or chain-ID guard in the ordinary proof path | Standard `0x1fffe000` mask |
| Susucoin | [Susucoin `af761617`](https://github.com/susucoin-project/susucoin/tree/af7616171f814313f8bdea43a3186ffaec9770f8) | No AuxPoW or chain-ID guard in the ordinary proof path | Standard `0x1fffe000` mask |
| Worldcoin | [Worldcoin `8a8da108`](https://github.com/OfficialWorldcoinGlobal/Worldcoin/blob/8a8da108323fd7c6b6a103cb25b5f54d427af0fb/src/worldcoin.cpp#L40-L67) | `src/worldcoin.cpp`: direct/legacy blocks return before the strict chain-ID guard | Standard `0x1fffe000` mask |
| Viacoin | [Viacoin `dc2fffb1`](https://github.com/viacoin/viacoin/tree/dc2fffb192af72e517c095e8ce539eb4365a128b) | `src/pow.cpp`: chain-ID validation is inside the AuxPoW-object branch | Standard `0x1fffe000` mask |
| NewYorkCoin | [NewYorkCoin `36ba3dfe`](https://github.com/jamesburrell2/newyorkcoin_v2/tree/36ba3dfef8df702cee992bd142ca7881025cec8d) | Chain-ID metadata exists, but the ordinary proof path does not enforce a strict version chain ID | Standard `0x1fffe000` mask |
| PACcoin | No authoritative, immutable daemon source could be established | Safety classification is unavailable; explorer metadata is not consensus evidence | Disabled; no mutable or consensus mask is claimed |

The PACcoin entry intentionally fails closed until an authoritative source and daemon-backed
submission contract can be reviewed. Its absence of a consensus mask means “unknown,” not “safe.”

Before this audit, 46 bundled Bitcoin-family entries had no `github` source field. The five
Smileycoin and three Pyrk entries now identify their reviewed sources, leaving 38 without that
metadata. Missing metadata is not itself evidence that a daemon reserves version bits, so it is not
safe to infer either a strict chain ID or a new mask from that absence. The survey classified
candidates with known Dogecoin/Luckycoin/AuxPoW lineage; groups that expose multiple templates; and
daemon code that selects consensus behavior from `nVersion`, including `BLOCK_VERSION_ALGO`,
`ALGO_VERSION_MASK`,
`GetPoWType`, and `GetPoWHash` dispatch. PACcoin is the unresolved candidate found by that review and
is disabled, but the catalogue set remains open rather than claiming that no other selector shape
exists. Future catalogue work must add immutable provenance and repeat the proof-path review before
assigning any other template an explicit mask.

## Miner behavior and troubleshooting

When rolling is enabled, Miningcore intersects the miner's requested mask with the template mask and
uses that exact negotiated result when validating submissions and constructing headers. A miner
cannot restore clipped chain-ID or algorithm bits by submitting `version_bits` outside that mask.

The BIP310 wire mask is an eight-digit, case-insensitive hexadecimal string. Miningcore also accepts
an optional `0x` prefix defensively. Malformed values, disjoint masks, and template-disabled rolling
return `"version-rolling": false` without a `version-rolling.mask`; the server log records the
specific reason. Returning a boolean avoids nonconforming clients treating a diagnostic string as a
truthy success value and then hashing with an unnegotiated mask. Most BIP310 clients then submit
ordinary work. If a miner disconnects or refuses jobs, update its firmware or disable its
requirement for version rolling; do not weaken the coin template. For custom templates, startup
diagnostics name
malformed, contradictory, oversized, or overlapping masks. Correct the source-reviewed contract
rather than bypassing the check.

After changing a custom mask, test the exact daemon revision on a private network: negotiate the
mask, submit explicit-zero and nonzero rolled versions, find a network-target header, submit it to
the daemon, and confirm the accepted header preserves every consensus-owned bit.
