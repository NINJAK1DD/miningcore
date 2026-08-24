#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <ios>
#include <iterator>
#include <string>
#include <vector>

#include "serialization/variant.h"
#include "serialization/binary_utils.h"
#include "cryptonote_core/cryptonote_basic.h"
#include "ringct/rctTypes.h"

extern "C" bool convert_blob_export(const char *, unsigned int, unsigned char *,
  unsigned int *, unsigned int);
extern "C" bool get_block_id_export(const char *, unsigned int, unsigned char *,
  unsigned int);

namespace
{
void require(bool condition)
{
  if(!condition)
    std::abort();
}

template<typename Proof>
Proof proof_with_rounds(size_t rounds)
{
  Proof proof;
  proof.L.resize(rounds);
  proof.R.resize(rounds);
  return proof;
}

std::string malformed_non_null_miner_block(uint8_t proof_type)
{
  cryptonote::block block{};
  block.set_blob_type(BLOB_TYPE_CRYPTONOTE);
  block.major_version = 1;
  block.minor_version = 1;
  block.miner_tx.version = 2;
  block.miner_tx.vin.push_back(cryptonote::txin_gen{0});

  cryptonote::tx_out output{};
  output.amount = 0;
  output.target = cryptonote::txout_to_key{crypto::public_key{}};
  block.miner_tx.vout.push_back(output);
  block.miner_tx.rct_signatures.type = proof_type;
  block.miner_tx.rct_signatures.ecdhInfo.resize(1);
  block.miner_tx.rct_signatures.outPk.resize(1);

  std::string blob;
  require(!serialization::dump_binary(block, blob));
  require(!blob.empty());
  return blob;
}

void require_parser_rejects(const std::string &blob)
{
  unsigned char converted[64];
  std::fill(std::begin(converted), std::end(converted), 0xa5);
  unsigned int converted_size = sizeof(converted);
  require(!convert_blob_export(blob.data(), blob.size(), converted, &converted_size, 0));
  require(converted_size == 0);

  unsigned char block_id[32];
  std::fill(std::begin(block_id), std::end(block_id), 0xa5);
  require(!get_block_id_export(blob.data(), blob.size(), block_id, 0));
  require(std::all_of(std::begin(block_id), std::end(block_id),
    [](unsigned char value) { return value == 0; }));
}
}

int main()
{
  // These helpers run while parsing daemon-supplied non-null RingCT data. The
  // subprocess must exit normally for malformed shapes instead of unwinding a
  // C++ exception through an extern "C"/P/Invoke boundary.
  require(rct::n_bulletproof_max_amounts(std::vector<rct::Bulletproof>{}) == 0);
  require(rct::n_bulletproof_plus_max_amounts(
    std::vector<rct::BulletproofPlus>{}) == 0);

  auto bulletproof = proof_with_rounds<rct::Bulletproof>(6);
  auto bulletproof_plus = proof_with_rounds<rct::BulletproofPlus>(10);
  require(rct::n_bulletproof_max_amounts(
    std::vector<rct::Bulletproof>{bulletproof}) == 1);
  require(rct::n_bulletproof_plus_max_amounts(
    std::vector<rct::BulletproofPlus>{bulletproof_plus}) == 16);

  bulletproof.R.pop_back();
  bulletproof_plus.L.emplace_back();
  require(rct::n_bulletproof_max_amounts(
    std::vector<rct::Bulletproof>{bulletproof}) == 0);
  require(rct::n_bulletproof_plus_max_amounts(
    std::vector<rct::BulletproofPlus>{bulletproof_plus}) == 0);

  // Serialize deliberately incomplete miner transactions far enough to carry
  // a non-null proof type and an empty proof vector. Deserialization reaches
  // the exact sizing helpers that previously threw across the C ABI.
  require_parser_rejects(malformed_non_null_miner_block(rct::RCTTypeBulletproof));
  require_parser_rejects(
    malformed_non_null_miner_block(rct::RCTTypeBulletproofPlus));

  return 0;
}
