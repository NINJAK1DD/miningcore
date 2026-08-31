#include <cstdint>

#include "../libmultihash/hashodo.h"

#ifdef _WIN32
#define MODULE_API __declspec(dllexport)
#else
#define MODULE_API __attribute__((visibility("default")))
#endif

extern "C" MODULE_API int odocrypt_export(const char* input, char* output,
    uint32_t input_len, uint32_t key)
{
    if(input == nullptr || output == nullptr || input_len != OdoCrypt::DIGEST_SIZE)
        return 0;

    try
    {
        odocrypt_hash(input, output, input_len, key);
        return 1;
    }
    catch(...)
    {
        return 0;
    }
}
