# Additional native portability vectors

These are fixed regression vectors for the exported library paths. Expected values
were recorded with the independently digest-verified Ubuntu 22.04 artifact
`10574790253` for PR head `e7deb94551cd45fdf396b094e9014c747b78db87`, then checked
against both the physical CPU and the strict emulated baseline. They are not
generated from the binary under test at runtime.

## VerusHash 2.2

Input is 80 bytes with values `00` through `4f`, passed to `verushash2b2o_export`.
Expected hash: `b670b52b5d8afbbe5418a2e1a6465f58f1ccf6a9d3a167fb888c956505d2d3a7`.
This export initializes the dispatch and executes Haraka/CLHash, with the host's
optimized implementation and the pre-AVX CPU's portable implementation. The input
is a synthetic hash vector, not a claim of a mined Verus block.

## Zano mainnet genesis

`zano-genesis.h` contains the positive serialized block assembled from the vendored
`src/Native/libzanonote/currency_core/genesis.cpp` mainnet transaction and the
`generate_genesis_block` fields: version 1, nonce `101011010121 + 84`, zero previous
hash, minor version/timestamp/flags zero, and no additional transactions. The 161
64-bit transaction words are serialized little-endian, followed by its seven
remaining bytes. The block's field order comes from `currency_basic.h`.

- Block ID: `cc608f59f8080e2fbfe3c8c80eb6e6a953d47cf2d6aebd345bada3a1cab99852`.
- Mining hash: `2f4667d8e7190a7fcc1e5de7e93d6c97fe9e883caed5ab35281f9a9deef5c595`.
- The probe also checks the exact 77-byte hashing blob and a byte-identical block
  serialization round trip through `convert_block_export` with the genesis nonce.

The two IDs differ intentionally: the mining hash zeros the nonce before hashing;
the block-ID operation hashes the serialized hashing-blob object. These positive
cases exercise parsing, transaction/tree hashing and serialization in all four
exports. Malformed truncated-varint checks remain separate. This does not claim
exhaustive coverage of all transaction versions or consensus paths.
