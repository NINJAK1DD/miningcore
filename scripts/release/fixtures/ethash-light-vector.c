#include <stdint.h>
#include <stdio.h>

#include "internal.h"

static void print_hash(const ethash_h256_t* hash)
{
    for(size_t i = 0; i < sizeof(hash->b); i++)
        printf("%02x", hash->b[i]);
}

int main(void)
{
    node cache_node = { 0 };
    ethash_h256_t header = { { 0 } };
    struct ethash_light light = {
        .cache = &cache_node,
        .cache_size = sizeof(cache_node),
        .block_number = 0,
    };

    for(size_t i = 0; i < sizeof(cache_node.bytes); i++)
        cache_node.bytes[i] = (uint8_t)(i * 3U + 1U);

    for(size_t i = 0; i < sizeof(header.b); i++)
        header.b[i] = (uint8_t)(i * 5U + 2U);

    const ethash_return_value_t result = ethash_light_compute_internal(
        &light, ETHASH_MIX_BYTES, header, UINT64_C(0x0123456789abcdef));

    if(!result.success)
    {
        fputs("Ethash-family light hash failed\n", stderr);
        return 1;
    }

    print_hash(&result.result);
    putchar(' ');
    print_hash(&result.mix_hash);
    putchar('\n');
    return 0;
}
