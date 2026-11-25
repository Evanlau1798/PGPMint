#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include "PGPMintMetal.h"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <exception>
#include <memory>
#include <mutex>

static_assert(sizeof(PGPMintMetalAdapter) == 280, "Managed adapter ABI mismatch");
static_assert(sizeof(PGPMintMetalDispatch) == 32, "Managed dispatch ABI mismatch");

namespace {
struct MetalContext {
    __strong id<MTLDevice> device;
    __strong id<MTLCommandQueue> queue;
    __strong id<MTLComputePipelineState> pipeline;
    __strong id<MTLBuffer> schedule;
    __strong id<MTLBuffer> parameters;
    __strong id<MTLBuffer> results;
    NSUInteger group_size = 0;
    bool has_schedule = false;
    std::mutex gate;
};

int32_t fail(char *error, uint32_t capacity, const char *message, int32_t status = -1) {
    if (error && capacity) std::snprintf(error, capacity, "%s", message ? message : "Unknown Metal error");
    return status;
}

template <typename Action>
int32_t guarded(char *error, uint32_t capacity, Action action) {
    if (error && capacity) error[0] = 0;
    @autoreleasepool {
        @try {
            try { return action(); }
            catch (const std::exception &e) { return fail(error, capacity, e.what()); }
            catch (...) { return fail(error, capacity, "Unexpected native Metal failure"); }
        } @catch (NSException *e) {
            return fail(error, capacity, e.reason.UTF8String);
        }
    }
}
}

int32_t pgpmint_metal_enumerate(PGPMintMetalAdapter *adapters, uint32_t capacity,
    uint32_t *count, char *error, uint32_t error_capacity) {
    return guarded(error, error_capacity, [&]() -> int32_t {
        if (!count || (capacity && !adapters))
            return fail(error, error_capacity, "Invalid enumeration buffer");
        NSArray<id<MTLDevice>> *devices = MTLCopyAllDevices();
        if (devices.count > UINT32_MAX) return fail(error, error_capacity, "Too many Metal devices");
        *count = static_cast<uint32_t>(devices.count);
        for (uint32_t i = 0; i < std::min(capacity, *count); ++i) {
            id<MTLDevice> device = devices[i];
            PGPMintMetalAdapter &adapter = adapters[i];
            std::memset(&adapter, 0, sizeof(adapter));
            adapter.registry_id = device.registryID;
            adapter.working_set_bytes = device.recommendedMaxWorkingSetSize;
            adapter.unified_memory = device.hasUnifiedMemory ? 1 : 0;
            adapter.low_power = device.lowPower ? 1 : 0;
            const char *name = device.name.UTF8String;
            std::snprintf(adapter.name, sizeof(adapter.name), "%s", name ? name : "Metal GPU");
        }
        return 0;
    });
}

int32_t pgpmint_metal_create(uint64_t registry_id, uint32_t requested_workgroup_size, const char *source,
    void **session, uint32_t *workgroup_size, char *error, uint32_t error_capacity) {
    if (session) *session = nullptr;
    if (workgroup_size) *workgroup_size = 0;
    return guarded(error, error_capacity, [&]() -> int32_t {
        if (!session || !workgroup_size || !source || !registry_id || !requested_workgroup_size)
            return fail(error, error_capacity, "Invalid Metal session arguments");
        auto context = std::make_unique<MetalContext>();
        for (id<MTLDevice> device in MTLCopyAllDevices()) {
            if (device.registryID == registry_id) { context->device = device; break; }
        }
        if (!context->device) return fail(error, error_capacity, "Metal registry ID is no longer available");
        NSString *text = [NSString stringWithUTF8String:source];
        if (!text) return fail(error, error_capacity, "Kernel source is not valid UTF-8");
        NSError *native_error = nil;
        id<MTLLibrary> library = [context->device newLibraryWithSource:text options:nil error:&native_error];
        if (!library) return fail(error, error_capacity, native_error.localizedDescription.UTF8String);
        id<MTLFunction> function = [library newFunctionWithName:@"search"];
        if (!function) return fail(error, error_capacity, "Metal kernel function 'search' is missing");
        context->pipeline = [context->device newComputePipelineStateWithFunction:function error:&native_error];
        if (!context->pipeline) return fail(error, error_capacity, native_error.localizedDescription.UTF8String);
        context->queue = [context->device newCommandQueue];
        context->schedule = [context->device newBufferWithLength:PGPMINT_METAL_SCHEDULE_WORDS * sizeof(uint32_t)
            options:MTLResourceStorageModeShared];
        context->parameters = [context->device newBufferWithLength:sizeof(PGPMintMetalDispatch)
            options:MTLResourceStorageModeShared];
        if (!context->queue || !context->schedule || !context->parameters)
            return fail(error, error_capacity, "Failed to allocate persistent Metal queue or buffers");
        NSUInteger limit = std::min<NSUInteger>(requested_workgroup_size, context->pipeline.maxTotalThreadsPerThreadgroup);
        NSUInteger width = context->pipeline.threadExecutionWidth;
        context->group_size = width && limit >= width ? (limit / width) * width : limit;
        if (!context->group_size) return fail(error, error_capacity, "Metal pipeline has no usable threadgroup size");
        *workgroup_size = static_cast<uint32_t>(context->group_size);
        *session = context.release();
        return 0;
    });
}

int32_t pgpmint_metal_dispatch(void *session, const uint32_t *schedule, uint32_t schedule_words,
    const PGPMintMetalDispatch *p, uint32_t *results, uint64_t result_words,
    char *error, uint32_t error_capacity) {
    return guarded(error, error_capacity, [&]() -> int32_t {
        if (!session || !schedule || !p || !results || schedule_words != PGPMINT_METAL_SCHEDULE_WORDS)
            return fail(error, error_capacity, "Invalid Metal dispatch buffers");
        if (!p->count || uint64_t(p->first) + p->count > (uint64_t(1) << 32)
            || uint64_t(p->count) * 4 > (uint64_t(1) << 27)
            || !p->capacity || p->capacity > (INT32_MAX / 16) - 1 || p->result_mode > 1
            || (p->result_mode == 1 && p->capacity != 1)
            || (p->target_low & ~p->mask_low) || (p->target_high & ~p->mask_high)
            || result_words != 4 * (uint64_t(p->capacity) + 1))
            return fail(error, error_capacity, "Invalid Metal dispatch range, mode or capacity");
        auto *context = static_cast<MetalContext *>(session);
        std::lock_guard<std::mutex> lock(context->gate);
        uint64_t bytes = result_words * sizeof(uint32_t);
        if (bytes > context->device.maxBufferLength)
            return fail(error, error_capacity, "Metal result buffer exceeds device maxBufferLength");
        if (!context->results || context->results.length < bytes) {
            id<MTLBuffer> buffer = [context->device newBufferWithLength:static_cast<NSUInteger>(bytes)
                options:MTLResourceStorageModeShared];
            if (!buffer) return fail(error, error_capacity, "Failed to allocate Metal result buffer");
            context->results = buffer;
        }
        size_t schedule_bytes = schedule_words * sizeof(uint32_t);
        if (!context->has_schedule || std::memcmp(context->schedule.contents, schedule, schedule_bytes) != 0) {
            std::memcpy(context->schedule.contents, schedule, schedule_bytes);
            context->has_schedule = true;
        }
        std::memcpy(context->parameters.contents, p, sizeof(*p));
        std::memset(context->results.contents, 0, static_cast<size_t>(bytes));
        id<MTLCommandBuffer> command = [context->queue commandBuffer];
        if (!command) return fail(error, error_capacity, "Failed to allocate Metal command buffer");
        id<MTLComputeCommandEncoder> encoder = [command computeCommandEncoder];
        if (!encoder) return fail(error, error_capacity, "Failed to allocate Metal compute encoder");
        [encoder setComputePipelineState:context->pipeline];
        [encoder setBuffer:context->schedule offset:0 atIndex:0];
        [encoder setBuffer:context->results offset:0 atIndex:1];
        [encoder setBuffer:context->parameters offset:0 atIndex:2];
        uint64_t per_group = context->group_size * 8;
        NSUInteger groups = static_cast<NSUInteger>(std::min<uint64_t>(65535, (p->count + per_group - 1) / per_group));
        [encoder dispatchThreadgroups:MTLSizeMake(groups, 1, 1)
            threadsPerThreadgroup:MTLSizeMake(context->group_size, 1, 1)];
        [encoder endEncoding];
        [command commit];
        [command waitUntilCompleted];
        if (command.status != MTLCommandBufferStatusCompleted)
            return fail(error, error_capacity, command.error.localizedDescription.UTF8String, -2);
        std::memcpy(results, context->results.contents, static_cast<size_t>(bytes));
        return 0;
    });
}

void pgpmint_metal_destroy(void *session) {
    @autoreleasepool { delete static_cast<MetalContext *>(session); }
}
