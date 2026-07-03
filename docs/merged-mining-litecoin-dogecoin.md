# Litecoin–Dogecoin merged mining

Miningcore can run Litecoin as the parent Scrypt pool and submit the same proof of work to Dogecoin through AuxPoW.

This version is limited to SOLO. Configure Litecoin and Dogecoin as separate enabled pools with unique pool IDs, both using `SOLO` payment processing. The Dogecoin pool supplies its daemon, wallet address, block classification and payout pipeline. The Litecoin pool references it:

```json
"mergedMining": {
  "enabled": true,
  "auxPoolId": "doge-solo",
  "addressParameter": "doge",
  "requireAuxAddress": true
}
```

Connect miners only to the Litecoin Stratum endpoint.

- Username: `LTC_ADDRESS.worker`
- Password: `d=65536;doge=DOGE_ADDRESS`

The Dogecoin daemon mines rewards to the configured Dogecoin pool wallet. Miningcore records the password-supplied Dogecoin address as the SOLO beneficiary and pays it through the existing Dogecoin payout processor after maturity.

A new parent job is generated when either chain changes. Each submitted Scrypt proof is checked against both targets. Litecoin and Dogecoin block submissions are independent. Accepted Dogecoin blocks are sent through the share message pipeline as block-only candidates under the Dogecoin pool id. They create a Dogecoin block record without inserting a synthetic Dogecoin share row.

When `requireAuxAddress` is true, authorisation fails if the address is missing or rejected by Dogecoin's `validateaddress` RPC. The Dogecoin pool must remain enabled so its normal classifier, maturity checks and payout processor can handle auxiliary blocks.

The parent pool polls both templates even when a Bitcoin Template Stream is configured, because parent-chain notifications do not include Dogecoin tip changes. `addressParameter` cannot be `d` or contain `;` or `=`.

## Pre-production validation

The automated suite verifies the AuxPoW byte layout, password parsing, relay compatibility, and that an accepted auxiliary candidate creates a block without a synthetic share row. It does not launch Litecoin Core or Dogecoin Core.

Before enabling mainnet traffic, run a daemon-backed regtest with real `litecoind` and `dogecoind` processes:

1. Create and fund/mature regtest wallets on both nodes, then configure distinct enabled LTC and DOGE SOLO pools.
2. Connect a Scrypt miner only to the LTC Stratum port using `d=<difficulty>;doge=<address>`.
3. Advance only the Dogecoin tip and confirm Miningcore broadcasts a fresh combined job without waiting for the Litecoin tip to change.
4. Submit an auxiliary solution and confirm `submitauxblock` accepts it, one pending DOGE block row is created, and no synthetic DOGE row is added to `shares`.
5. Mature the DOGE coinbase and confirm the normal DOGE classifier credits and pays the password-supplied beneficiary.
6. Repeat with a parent-chain solution and with a solution meeting both targets to confirm LTC and DOGE submissions remain independent.
