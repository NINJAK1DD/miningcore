#include <ios>
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
}

std::string epee::to_hex::string(const epee::span<const std::uint8_t>)
{
  throw std::runtime_error(error_message);
}

size_t rct::n_bulletproof_max_amounts(const std::vector<rct::Bulletproof> &)
{
  throw std::runtime_error(error_message);
}

size_t rct::n_bulletproof_plus_max_amounts(
  const std::vector<rct::BulletproofPlus> &)
{
  throw std::runtime_error(error_message);
}

extern "C" void cn_slow_hash(const void *, size_t, char *)
{
  throw std::runtime_error(error_message);
}
