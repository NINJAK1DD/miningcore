# Additional native portability vectors

These are fixed regression vectors for the exported library paths. The synthetic
Verus vector and Zano mining hash/hashing blob were recorded with the
independently digest-verified Ubuntu 22.04 artifact
`10574790253` for PR head `e7deb94551cd45fdf396b094e9014c747b78db87`, then checked
against both the physical CPU and the strict emulated baseline. The additional
Verus PBaaS vector and Zano genesis block ID have independent upstream expected
values cited below. No expected values are generated from the binary under test
at runtime.

## Blake3 multi-chunk dispatch

The portability probe hashes 16,385 bytes with byte `i` equal to `i % 251`, unkeyed
and keyed with the ASCII key `whats the Elvish word for friend`. The 32-byte results are:

- Unkeyed: `1dabe216be2578830263b049de1639f39f05a4da616b9b78c7a5e4e41662fd1f`.
- Keyed: `9f1e8750b485576aa8af42e403bfb340665b45a952799d0155756c994db7dfd2`.

The input pattern and key follow the [pinned official BLAKE3 vectors](https://github.com/BLAKE3-team/BLAKE3/blob/f55849f89cd85083c1c9daa2c5de20766f309c00/test_vectors/test_vectors.json).
That file does not contain this length. These additional constants are checked
against the independent BouncyCastle implementation by `Blake3NativeCapabilityTests`;
they are never derived from the native binary being tested. Seventeen chunks
exercise a full wide SIMD batch plus its tail. CI runs the actual exported wrapper
on both the host CPU and constrained QEMU CPUs. Startup additionally checks official
0, 3, 64, 65, 1024 and 1025-byte keyed/unkeyed vectors.

## VerusHash 2.2

Input is 80 bytes with values `00` through `4f`, passed to `verushash2b2o_export`.
Expected hash: `b670b52b5d8afbbe5418a2e1a6465f58f1ccf6a9d3a167fb888c956505d2d3a7`.
This export initializes the dispatch and executes Haraka/CLHash, with the host's
optimized implementation and the pre-AVX CPU's portable implementation. The input
is a synthetic hash vector, not a claim of a mined Verus block.

`verus-v22.h` additionally copies the 1,487 bytes of `pbaas_header` from
[VerusCoin/verushashpy tests/verus_hash.py at 7bfc08b97b616eedd3b056ac7ca0c51d410344b1](https://github.com/VerusCoin/verushashpy/blob/7bfc08b97b616eedd3b056ac7ca0c51d410344b1/tests/verus_hash.py#L1364).
Its published `pbaas_header_expected` is
`0000008909746e856846d74bb6e1ac5aadd9779646c94d8d85f523bbe3fe0135`.
Upstream displays that block ID in reverse byte order; the probe compares the
32 raw output bytes against
`3501fee3bb23f5858d4dc9469677d9ad5aace1b64bd74668856e740989000000`.
The header bytes are copied unchanged, and the upstream MIT notice is retained.

This case uses `verushash2b2_export`, which canonicalizes PBaaS header data before
hashing, matching the upstream wrapper's `CBlockHeader::GetVerusV2Hash` operation.
The raw `verushash2b2o_export` intentionally does not perform that step and is used
only for the synthetic vector. The upstream expected value therefore checks both
consensus header processing and the dispatched hash on host and baseline CPUs.

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

The block ID independently matches Zano's `currency::gdefault_genesis` constant in
[currency_basic.h at 9a188e1531648a743e1b8633c24fa1e31be3f5e8](https://github.com/hyle-team/zano/blob/9a188e1531648a743e1b8633c24fa1e31be3f5e8/src/currency_core/currency_basic.h#L66).
This is an explicit consensus genesis reference, not the same hex string's reuse
as an unrelated example asset ID or key image in upstream documentation/tests.

The two IDs differ intentionally: the mining hash zeros the nonce before hashing;
the block-ID operation hashes the serialized hashing-blob object. These positive
cases exercise parsing, transaction/tree hashing and serialization in all four
exports. Malformed truncated-varint checks remain separate. This does not claim
exhaustive coverage of all transaction versions or consensus paths.
