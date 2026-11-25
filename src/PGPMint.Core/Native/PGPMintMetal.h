#pragma once
#include <stdint.h>

#if defined(__cplusplus)
extern "C" {
#endif

#define PGPMINT_METAL_EXPORT __attribute__((visibility("default")))
#define PGPMINT_METAL_SCHEDULE_WORDS (77 * 4 + 4)

typedef struct {
    uint64_t registry_id;
    uint64_t working_set_bytes;
    uint32_t unified_memory;
    uint32_t low_power;
    char name[256];
} PGPMintMetalAdapter;

typedef struct {
    uint32_t first, count, mask_low, target_low;
    uint32_t mask_high, target_high, capacity, result_mode;
} PGPMintMetalDispatch;

// 0 = success, -1 = operation/input failure, -2 = failed GPU command.
// Error text is UTF-8, truncated to capacity-1 and always NUL-terminated.
// All pointers are caller-owned; no pointer is retained except the returned session.
PGPMINT_METAL_EXPORT int32_t pgpmint_metal_enumerate(
    PGPMintMetalAdapter *adapters, uint32_t capacity, uint32_t *count,
    char *error, uint32_t error_capacity);

PGPMINT_METAL_EXPORT int32_t pgpmint_metal_create(
    uint64_t registry_id, uint32_t requested_workgroup_size, const char *source,
    void **session, uint32_t *workgroup_size,
    char *error, uint32_t error_capacity);

// Synchronous: all timestamps and all four lanes complete before readback.
// Schedules contain public SHA-1 words only. Dispose must not race another call.
PGPMINT_METAL_EXPORT int32_t pgpmint_metal_dispatch(
    void *session, const uint32_t *schedule, uint32_t schedule_words,
    const PGPMintMetalDispatch *parameters, uint32_t *results, uint64_t result_words,
    char *error, uint32_t error_capacity);

PGPMINT_METAL_EXPORT void pgpmint_metal_destroy(void *session);

#if defined(__cplusplus)
}
#endif
