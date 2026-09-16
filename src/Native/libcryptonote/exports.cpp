#include <cstring>
#include <limits>
#include <stdint.h>
#include <string>

#include "common/base58.h"
#include "cryptonote_core/cryptonote_basic.h"
#include "cryptonote_core/cryptonote_format_utils.h"
#include "crypto/hash-ops.h"
#include "serialization/binary_utils.h"

using namespace cryptonote;

#ifdef _WIN32
#define MODULE_API __declspec(dllexport)
#else
#define MODULE_API
#endif

extern "C" void cn_fast_hash(const void *data, size_t length, char *hash);

extern "C" MODULE_API bool convert_blob_export(
    const char *input, unsigned int inputSize, unsigned char *output,
    unsigned int *outputSize, unsigned int blobType)
{
    if(outputSize == nullptr)
        return false;

    const unsigned int originalOutputSize = *outputSize;
    *outputSize = 0;
    if(input == nullptr || output == nullptr)
        return false;

    try
    {
        const auto blob_type = static_cast<BLOB_TYPE>(blobType);
        const blobdata input_blob(input, inputSize);
        blobdata result;

        block parsed_block = AUTO_VAL_INIT(parsed_block);
        parsed_block.set_blob_type(blob_type);
        if(!parse_and_validate_block_from_blob(input_blob, parsed_block))
            return false;

        if(!get_block_hashing_blob(parsed_block, result))
            return false;
        if(result.length() > std::numeric_limits<unsigned int>::max())
            return false;

        *outputSize = static_cast<unsigned int>(result.length());
        if(result.length() > originalOutputSize)
            return false;

        std::memcpy(output, result.data(), result.length());
        return true;
    }
    catch (...)
    {
        *outputSize = 0;
        return false;
    }
}

extern "C" MODULE_API bool get_block_id_export(
    const char *input, unsigned int inputSize, unsigned char *output,
    unsigned int blobType)
{
    if(input == nullptr || output == nullptr)
        return false;

    std::memset(output, 0, 32);

    try
    {
        const auto blob_type = static_cast<BLOB_TYPE>(blobType);
        const blobdata input_blob(input, inputSize);
        crypto::hash block_id;

        block parsed_block = AUTO_VAL_INIT(parsed_block);
        parsed_block.set_blob_type(blob_type);
        if(!parse_and_validate_block_from_blob(input_blob, parsed_block))
            return false;

        if(!get_block_hash(parsed_block, block_id))
            return false;

        std::memcpy(output, reinterpret_cast<const char *>(&block_id), 32);
        return true;
    }
    catch (...)
    {
        std::memset(output, 0, 32);
        return false;
    }
}

extern "C" MODULE_API uint64_t decode_address_export(
    const char *input, unsigned int inputSize)
{
    if(input == nullptr)
        return 0L;

    try
    {
        const blobdata input_blob(input, inputSize);
        blobdata data;

        uint64_t prefix;
        if(!tools::base58::decode_addr(input_blob, prefix, data) || data.empty())
            return 0L;

        account_public_address address;
        if(!::serialization::parse_binary(data, address))
            return 0L;

        if(!crypto::check_key(address.m_spend_public_key) ||
            !crypto::check_key(address.m_view_public_key))
        {
            return 0L;
        }

        return prefix;
    }
    catch (...)
    {
        return 0L;
    }
}

extern "C" MODULE_API uint64_t decode_integrated_address_export(
    const char *input, unsigned int inputSize)
{
    if(input == nullptr)
        return 0L;

    try
    {
        const blobdata input_blob(input, inputSize);
        blobdata data;

        uint64_t prefix;
        if(!tools::base58::decode_addr(input_blob, prefix, data) || data.empty())
            return 0L;

        integrated_address address;
        if(!::serialization::parse_binary(data, address) ||
            !crypto::check_key(address.adr.m_spend_public_key) ||
            !crypto::check_key(address.adr.m_view_public_key))
        {
            return 0L;
        }

        return prefix;
    }
    catch (...)
    {
        return 0L;
    }
}

extern "C" MODULE_API void cn_fast_hash_export(
    const char *input, unsigned char *output, uint32_t inputSize)
{
    if(output == nullptr)
        return;

    std::memset(output, 0, 32);
    if(input == nullptr)
        return;

    try
    {
        cn_fast_hash(
            static_cast<const void *>(input), static_cast<size_t>(inputSize),
            reinterpret_cast<char *>(output));
    }
    catch (...)
    {
        std::memset(output, 0, 32);
    }
}
