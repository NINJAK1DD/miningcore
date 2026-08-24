#include <ios>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

#include "serialization/variant.h"
#include "serialization/serialization.h"
#include "hex.h"
#include "ringct/rctTypes.h"

namespace
{
constexpr auto error_message =
  "A parsing-only Miningcore cryptonote stub was called unexpectedly";

template<typename Proof>
size_t max_amounts_for_proof(const Proof &proof)
{
  // Monero's range-proof format uses six base bits and supports up to 16 outputs.
  // Invalid proof shapes are rejected by returning zero, which is what the
  // deserializer expects from these parsing helpers.
  constexpr size_t base_bits = 6;
  constexpr size_t max_extra_bits = 4;

  if(proof.L.size() < base_bits ||
    proof.L.size() > base_bits + max_extra_bits ||
    proof.L.size() != proof.R.size())
  {
    return 0;
  }

  return static_cast<size_t>(1) << (proof.L.size() - base_bits);
}

template<typename Proof>
size_t max_amounts_for_proofs(const std::vector<Proof> &proofs)
{
  size_t result = 0;

  for(const auto &proof : proofs)
  {
    const auto count = max_amounts_for_proof(proof);
    if(count == 0 || count > std::numeric_limits<size_t>::max() - result)
      return 0;

    result += count;
  }

  return result;
}
}

std::string epee::to_hex::string(const epee::span<const std::uint8_t> source)
{
  static constexpr char digits[] = "0123456789abcdef";
  if(source.size() > std::numeric_limits<size_t>::max() / 2)
    throw std::length_error("Hex input is too large");

  std::string result(source.size() * 2, '\0');

  for(size_t index = 0; index < source.size(); ++index)
  {
    result[index * 2] = digits[source[index] >> 4];
    result[index * 2 + 1] = digits[source[index] & 0x0f];
  }

  return result;
}

size_t rct::n_bulletproof_max_amounts(
  const std::vector<rct::Bulletproof> &proofs)
{
  return max_amounts_for_proofs(proofs);
}

size_t rct::n_bulletproof_plus_max_amounts(
  const std::vector<rct::BulletproofPlus> &proofs)
{
  return max_amounts_for_proofs(proofs);
}

extern "C" void cn_slow_hash(const void *, size_t, char *)
{
  throw std::runtime_error(error_message);
}
