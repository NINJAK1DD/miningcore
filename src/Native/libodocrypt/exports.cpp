#include <array>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>

#include "../libmultihash/odocrypt.h"
extern "C" {
#include "../libmultihash/KeccakP-800-SnP.h"
}

#ifdef _WIN32
#define MODULE_API __declspec(dllexport)
#else
#define MODULE_API __attribute__((visibility("default")))
#endif

namespace
{
    // Eight entries cover the built-in main/test/signet/regtest schedules for two
    // concurrently configured DigiByte deployments while keeping memory bounded.
    const std::size_t cache_size = 8;

    struct cache_entry
    {
        uint32_t key = 0;
        std::shared_ptr<const OdoCrypt> schedule;
    };

    std::array<cache_entry, cache_size> schedules;
    std::mutex schedules_mutex;
    std::size_t next_replacement = 0;

    std::shared_ptr<const OdoCrypt> get_schedule(uint32_t key)
    {
        std::lock_guard<std::mutex> lock(schedules_mutex);

        for(const auto& entry : schedules)
        {
            if(entry.schedule && entry.key == key)
                return entry.schedule;
        }

        auto result = std::make_shared<const OdoCrypt>(key);
        schedules[next_replacement].key = key;
        schedules[next_replacement].schedule = result;
        next_replacement = (next_replacement + 1) % cache_size;
        return result;
    }

    void hash_with_schedule(const char* input, char* output,
        const OdoCrypt& schedule)
    {
        char state[KeccakP800_stateSizeInBytes] = {};
        std::memcpy(state, input, OdoCrypt::DIGEST_SIZE);
        state[OdoCrypt::DIGEST_SIZE] = 1;
        schedule.Encrypt(state, state);
        KeccakP800_Permute_12rounds(state);
        std::memcpy(output, state, 32);
    }
}

extern "C" MODULE_API int odocrypt_export(const char* input, char* output,
    uint32_t input_len, uint32_t key)
{
    if(input == nullptr || output == nullptr || input_len != OdoCrypt::DIGEST_SIZE)
        return 0;

    try
    {
        const auto schedule = get_schedule(key);
        hash_with_schedule(input, output, *schedule);
        return 1;
    }
    catch(...)
    {
        return 0;
    }
}
