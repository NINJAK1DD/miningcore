# Custodial source compatibility inventory

Reviewed on 7 November 2026 for PR [#260](https://github.com/NINJAK1DD/miningcore/pull/260).
This is a source-discovery and routing inventory, not certification of every coin,
current mainnet, daemon binary or release. The [confirmation policy](custodial-confirmation-policy.md)
defines the actual settlement gate and operational response to incompatible RPCs.

## Scope and evidence levels

The committed [inventory](custodial-source-inventory.json) enumerates **180 templates**:
149 Bitcoin, 19 Progpow, ten Equihash, one Nexa and one Satoshicash. Progpow and
Satoshicash route through the Bitcoin handler; Equihash either selects it explicitly
or inherits its classification in `EquihashPayoutHandler`. Bitcoin BLAKE2b overrides
classification and is excluded.

- **172** have the expected wallet and block source fields located at immutable revisions.
- **One** (TDCoin) has matching source in a candidate repository whose upstream
  ownership still needs confirmation.
- **Seven** lack an authoritative source matching the configured template.
- **BTC, LTC, DOGE and Nexa** have live isolated-daemon regression evidence described
  in the policy. Representative Progpow, Satoshicash and both Equihash routing
  branches have HTTP decoder/handler tests; selected families also have PostgreSQL
  rollback, restart and replay coverage.

The scanner records repository revisions, exact source URLs, SHA-256 hashes and
located/missing field names. A quoted field appearing somewhere in a file does
**not** prove its type, value, applicability to every transaction, or runtime behavior.
Manual inspection of representative RPC implementations confirms the coinbase
`generated`, `blockindex`, `immature`/`generate` and block-transaction branches for
Ravencoin, Firo, Satoshicash, Zcash, Verus and Pirate in addition to the live families.
Verus also exposes a separate `mint` category; that is not accepted as a generated
pool coinbase. There is no generic fallback that weakens the settlement gate.

Representative revisions are recorded in the inventory:
Ravencoin `6d48ae0175b10283248146ae3080e2ba70966739`,
Firo `ea171d5c70780f1ab653162589b98a5cd12b77ab`,
Satoshicash `adf24717a49d8c9c52fa54e1b175641e652f87b5`,
Zcash `558f686599586f55def3db86955d74d3be44605e`,
Verus `5ec1d83b16253aa56abda5588be76ce2d84dee8f` and
Pirate `b14db80bfc1a819b958e36d1e0127e94c395d445`.

## Manual inspection results

The original scan had three apparent contract gaps. Bitcoin SV moved wallet
serialization and transaction listing into `src/wallet/wallet.cpp`; that supplemental
source contains the generated/index/category branches. Zero Dynamics Cash keeps
its RPC implementation in `src/rpc/wallet.cpp`. Flux's template links
`RunOnFlux/flux`, a frontend; the daemon is [fluxd](https://github.com/RunOnFlux/fluxd).
The corrected scan locates all required fields in those implementations.

Flux's current daemon has transitioned to PoUW v2, and several other upstreams
describe chain replacements or an end to PoW. Finding a repository therefore does
not establish that an old Miningcore mining template is suitable for today's chain.
Deployments must commission the exact daemon version and chain configuration.
The historical sources remain useful for checking the RPC contract without silently
changing mining algorithms or network parameters in this security fix.

## Recovered links and unresolved cases

The initial 40 templates without repository links yielded **33 matching project or
historical upstream repositories**, **one TDCoin candidate**, and **six unresolved
matching-source cases**. Separately, Datromax and Ultronai owner-only links were
resolved to daemon repositories, the Flux frontend link was corrected for this
audit, and historical Vkax source was recovered. Litecoin II's existing repository
remains unavailable, bringing the unresolved total to seven.

The [research record](custodial-repository-research.json) preserves provenance and
caveats. Links below identify source evidence, not download recommendations.
Recovered links are used by the audit; no coin algorithm, network parameter or
production template behavior is changed.

| Template | Repository or unresolved source | Provenance and qualification |
| --- | --- | --- |
| `bitcoin-private` | [BTCPrivate/BitcoinPrivate-legacy](https://github.com/BTCPrivate/BitcoinPrivate-legacy) | [Evidence](https://github.com/BTCPrivate/BitcoinPrivate) — Legacy Equihash chain; current repository explicitly describes a new chain. Use legacy source for the configured template. |
| `bitcoinnickel` | [bitcoinNickel/bitcoinnickel](https://github.com/bitcoinNickel/bitcoinnickel) | [Evidence](https://github.com/bitcoinNickel/bitcoinnickel/blob/master/README.md) — Project-owned client and build instructions; original project website was unavailable. |
| `bitflate` | [bitflate/bitflate](https://github.com/bitflate/bitflate) | [Evidence](https://bitcointalk.org/index.php?topic=5110495.0) — Project announcement and project repository. |
| `bithereum` | [BTHPOS/BTH](https://github.com/BTHPOS/BTH) | [Evidence](https://github.com/BTHPOS) — Project organization links bithereum.network. |
| `bitrae` | [bitraenet/bitrae](https://github.com/bitraenet/bitrae) | [Evidence](https://bitrae.net) — Project website links repository. |
| `browncoin` | [browncoin-project/Browncoin](https://github.com/browncoin-project/Browncoin) | [Project website](https://browncoin-project.github.io/) links this daemon and the v1.2 Verthash hard fork at block 400000, matching the template. |
| `cmusicai` | [CMUSICAI/Cmusic](https://github.com/CMUSICAI/Cmusic) | [Evidence](https://cmusic.ai) — Project website links wallet repository. |
| `cpuchain` | [cpuchain/cpuchain](https://github.com/cpuchain/cpuchain) | [Evidence](https://www.cpuchain.org) — Project website links releases. |
| `emrals` | Unresolved | [Evidence](https://github.com/Emrals/masternode) — Original Emrals/emrals is unavailable; jamesdwilson/emrals is a candidate archive, not verified as the current upstream. |
| `etitanium` | [titaniumblockchain/titanium](https://github.com/titaniumblockchain/titanium) | [Evidence](https://github.com/titaniumblockchain/titanium/blob/main/README.md) — Project README links etitanium.net, explorer and SourceForge distribution. |
| `geekcash` | [GeekCash/geek](https://github.com/GeekCash/geek) | [Evidence](https://github.com/GeekCash) — Project organization and repository link geekcash.org. |
| `gspcoin` | [gamgsp/gspcoin](https://github.com/gamgsp/gspcoin) | [Evidence](https://gspcoin.us/docs/GSPC-Whitepaper-v1.2.pdf) — Project whitepaper links releases; README names Global Social Post and current domain. |
| `hfrcoin` | [hfrcoin/HFRcoin](https://github.com/hfrcoin/HFRcoin) | [Evidence](https://bitcointalk.org/index.php?topic=2660287.0) — Project announcement; README links hfrco.in and Hardware.fr project thread. |
| `lanacoin` | [LanaCoin/lanacoin](https://github.com/LanaCoin/lanacoin) | [Evidence](https://lanacoin.com/wallets/) — Project wallet page; hybrid chain with an end to PoW, not current-mainnet mining certification. |
| `levocoin` | [levoserve/levocoin](https://github.com/levoserve/levocoin) | [Evidence](https://bitcointalk.org/index.php?topic=1626022) — Historical project announcement and source archive. |
| `microbitcoin` | [MicroBitcoinOrg/MicroBitcoin](https://github.com/MicroBitcoinOrg/MicroBitcoin) | [Evidence](https://github.com/MicroBitcoinOrg) — Official organization; current README describes a new network and UTXO snapshot. Source discovery is not legacy/mainnet compatibility certification. |
| `minexcoin` | [minexcoin/minexcoin](https://github.com/minexcoin/minexcoin) | [Evidence](https://github.com/minexcoin/minexcoin/blob/master/README.md) — Project organization; README links original project site and whitepaper. |
| `note-blockchain` | [note-llc/NoteBlockchain](https://github.com/note-llc/NoteBlockchain) | [Evidence](https://github.com/note-llc) — Project organization; README links notebc.com. |
| `paccoin` | [PACCommunity/PAC](https://github.com/PACCommunity/PAC) | [Evidence](https://bitcointalk.org/index.php?topic=3047868.0) — Historical X11 PAC upstream; distinct from the earlier PoS PAC and later PAC Protocol chain. No change to disabled version rolling. |
| `pakcoin` | [Pakcoin-project/pakcoinold](https://github.com/Pakcoin-project/pakcoinold) | [Evidence](https://bitcointalk.org/index.php?topic=1096893.0) — Historical PoW source; current pakcoin repository is a PoS transition, not this mining template. |
| `picacoin` | [picacoin/picacoin](https://github.com/picacoin/picacoin) | [Evidence](https://github.com/picacoin/picacoin/blob/main/README.md) — Project organization and original whitepaper link. |
| `polcoin` | [pdrobek/polcoin-new](https://github.com/pdrobek/polcoin-new) | [Evidence](https://bitcointalk.org/index.php?topic=945549.0) — Project announcement and README match polcoin.pl specifications and new-chain identity. |
| `profithunters` | [profithunterscoin/phc](https://github.com/profithunterscoin/phc) | [Evidence](https://bitcointalk.org/index.php?topic=2786295.0) — Project announcement links repository; historical hybrid chain. |
| `rincoin` | [Rin-coin/rincoin](https://github.com/Rin-coin/rincoin) | [Evidence](https://coin.rin.so/) — RinHash project; repository Discord invitation matches template. |
| `rosecoin` | [rocheston/rosecoin](https://github.com/rocheston/rosecoin) | [Evidence](https://github.com/rocheston/rosecoin/blob/master/README.md) — Rocheston project source explicitly identifies ROC/Scrypt, not similarly named tokens. |
| `shroud` | Unresolved | [Evidence](https://github.com/ShroudProtocol/ShroudX) — Historical upstream unavailable; xsertyPool/ShroudX matches x16rv2 but remains an unverified mirror. |
| `smartiecoin` | [SmartiesCoin/Smartiecoin](https://github.com/SmartiesCoin/Smartiecoin) | [Evidence](https://smartiecoin.com) — Project site and repository; chain revisions require separate commissioning. |
| `sparkspay` | [sparkspay/sparks](https://github.com/sparkspay/sparks) | [Evidence](https://sparkspay.io/masternode-guide) — Project guide links releases. |
| `swippcoin` | Unresolved | [Evidence](https://bitcointalk.org/index.php?topic=2152304.20) — Historical swippcoin/swipp unavailable; swipis/swipp is empty. No verified replacement. |
| `tdcoin` | [TDCoin-Dev-Team/TDCoinCore](https://github.com/TDCoin-Dev-Team/TDCoinCore) | [Evidence](https://www.tdcoincore.org/) — Candidate only: nested tdcoin-0.18.0 source matches project chain parameters; maintainer ownership not independently established. tatdig/TDCoin contains project assets, not daemon code. |
| `theholyroger` | [TheHolyRogerCoin/TheHolyRogerCoin](https://github.com/TheHolyRogerCoin/TheHolyRogerCoin) | [Evidence](https://theholyroger.com/The_Holy_Roger_Coin/) — Project website links repository. |
| `tiltcoin` | [TILTCoinIO/TILT](https://github.com/TILTCoinIO/TILT) | [Evidence](https://github.com/TILTCoinIO/TILT) — Project-owned daemon located; corroborated by historical mining directory. Exact release still needs live commissioning. |
| `titcoin` | [titcoin/titcoin](https://github.com/titcoin/titcoin) | [Evidence](https://www.titcoinproject.org/download/) — Project download page; daemon README identifies current wallet and titcoin.github.io. |
| `vishai` | [vishcoin/vishai](https://github.com/vishcoin/vishai) | [Evidence](https://bitcointalk.org/index.php?topic=5479848.0) — Project announcement and daemon README. |
| `voltpotcoin` | Unresolved | [Evidence](https://github.com/ekkakon/vpc) — Historical source link recovered from listings but repository unavailable; project website unavailable. |
| `widecoin` | Unresolved | [Evidence](https://github.com/widecoin-project) — Original widecoin-project/widecoin unavailable. widecore is a JavaScript stack, not the daemon. |
| `woodcoin` | [funkshelper/woodcoin](https://github.com/funkshelper/woodcoin) | [Evidence](https://archive.woodcoin.org) — Project archive identifies maintainer source; historical full node. |
| `xazab` | [xazabevo/xazab](https://github.com/xazabevo/xazab) | [Evidence](https://github.com/xazabevo/xazab/blob/master/README.md) — Project organization and X11 daemon; also xazab-project/xazab v0.18. Exact deployed release requires commissioning. |
| `xenios` | Unresolved | [Evidence](https://github.com/xeniosproject/xenioscoin) — Original upstream named in project presentation is unavailable; third-party copies not substituted. |
| `yenten` | [yentencoin/yenten](https://github.com/yentencoin/yenten) | [Evidence](https://yentencoin.info) — Project website explicitly supplies clone URL. |
| `datromax` | [datromax/datromax](https://github.com/datromax/datromax) | [Evidence](https://github.com/datromax) — Existing template links owner only; actual project daemon found under that owner. |
| `ultronai` | [aidevpin/core](https://github.com/aidevpin/core) | [Evidence](https://github.com/aidevpin) — Existing template links owner only; core README identifies Ultronai. |
| `flux` | [RunOnFlux/fluxd](https://github.com/RunOnFlux/fluxd) | [Evidence](https://github.com/RunOnFlux/fluxd/blob/master/README.md) — Existing link RunOnFlux/flux is frontend. fluxd is daemon; HEAD has PoUW v2, so discovery is not current Equihash mining certification. |
| `litecoin-ii` | Unresolved | [Evidence](https://github.com/litecoinII-project/litecoinII) — Existing repository returns 404; no authoritative replacement established. |
| `vkax` | [vkaxproject/vkax](https://github.com/vkaxproject/vkax) | [Evidence](https://github.com/vkaxproject/vkax/blob/master/README.md) — Historical upstream says development moved to vkaxcore/VKAX, which is unavailable. Audit historical source only. |

Unavailable originals and unverified mirrors are deliberately not treated as
authoritative replacements. Browncoin's current project website resolves the
Verthash lineage; the earlier Scrypt repository was not substituted. Widecore is a
JavaScript stack rather than the Widecoin daemon. TDCoin's candidate source matches the public genesis,
reward, port and address-prefix information but still lacks an independently
established maintainer relationship.

## Reproduce and interpret the inventory

Run from the repository root with Python 3 and Git, using public HTTPS access:

```sh
python3 scripts/release/audit-custodial-sources.py
```

The default output is `build/custodial-source-audit/inventory.json`; it does not
overwrite the reviewed inventory. To refresh the committed record deliberately:

```sh
python3 scripts/release/audit-custodial-sources.py --output docs/custodial-source-inventory.json
```

The tool executes no downloaded source or binaries. It resolves upstream HEAD,
downloads bounded source files and records the immutable revision used. HEAD can
change, and temporary network errors are not proof of incompatibility. Review
differences and perform pinned live tests before treating a newly located source
as operational support. All unresolved or malformed RPC contracts continue to fail
closed with the custodial warning and delayed alert.
