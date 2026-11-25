using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed unsafe partial class VulkanDevice
{
    public IReadOnlyList<uint>[] ScanGroup(
        IReadOnlyList<byte[]> publicKeys,
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
        int capacity = 1024) =>
        ScanBatch(publicKeys, startTimestamp, timestampCount, pattern, capacity).Hits;

    public SearchBatchResult ScanBatch(
        IReadOnlyList<byte[]> publicKeys,
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
        int capacity = 1024,
        SearchResultMode resultMode = SearchResultMode.EnumerateAll,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentNullException.ThrowIfNull(pattern);

        if (publicKeys.Count != FourKeyCount)
        {
            throw new ArgumentException($"Expected exactly {FourKeyCount} public keys.", nameof(publicKeys));
        }

        if (timestampCount == 0 || (ulong)startTimestamp + timestampCount > (1UL << 32))
        {
            throw new ArgumentOutOfRangeException(nameof(timestampCount), "Timestamp range exceeds uint32 bounds.");
        }

        if ((long)timestampCount * FourKeyCount > MaxCandidatesPerBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampCount), "Batch exceeds device MaxCandidatesPerBatch.");
        }

        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        int resultCapacity = resultMode switch
        {
            SearchResultMode.FindOne => 1,
            SearchResultMode.EnumerateAll => capacity,
            _ => throw new ArgumentOutOfRangeException(nameof(resultMode))
        };

        if (cancellationToken.IsCancellationRequested)
        {
            return SearchBatchResult.CancelledBeforeDispatch(FourKeyCount);
        }

        PatternPlan plan = PatternPlan.From(pattern);
        UploadKeySchedulesIfNeeded(publicKeys);
        EnsureResultsBuffer(resultCapacity);
        int distinctKeys = GpuSearchBatch.CountDistinctKeys(publicKeys);
        return GpuSearchBatch.Run(FourKeyCount, distinctKeys, startTimestamp, timestampCount, resultCapacity, resultMode, cancellationToken,
            (first, count) => Dispatch(first, count, plan, resultCapacity, resultMode));
    }

    private uint[] Dispatch(
        uint startTimestamp,
        uint timestampCount,
        PatternPlan plan,
        int capacity,
        SearchResultMode resultMode)
    {
        int strideUints = capacity + 1;
        int totalUints = FourKeyCount * strideUints;

        bool clearedOnHost = _counterMayBeDirty;
        if (clearedOnHost)
        {
            new Span<uint>((void*)_ssboMapped, totalUints).Clear();
            if (!_ssboCoherent)
            {
                var flushRange = new VulkanNative.VkMappedMemoryRange
                {
                    SType = VulkanNative.VkStructureTypeMappedMemoryRange,
                    Memory = _ssboMemory,
                    Offset = 0,
                    Size = VulkanNative.VkWholeSize
                };
                CheckVk(VulkanNative.vkFlushMappedMemoryRanges(_device, 1, &flushRange), "vkFlushMappedMemoryRanges(ssbo)");
            }
        }

        _counterMayBeDirty = true;

        ulong desiredGroups = Math.Max(1UL, ((ulong)timestampCount + _candidatesPerGroup - 1UL) / _candidatesPerGroup);
        uint dispatchGroups = (uint)Math.Clamp(desiredGroups, 1UL, _maxDispatchGroupsX);
        uint totalThreads = dispatchGroups * _workgroupSize;

        var cbbi = new VulkanNative.VkCommandBufferBeginInfo
        {
            SType = VulkanNative.VkStructureTypeCommandBufferBeginInfo,
            Flags = VulkanNative.VkCommandBufferUsageOneTimeSubmitBit
        };
        CheckVk(VulkanNative.vkBeginCommandBuffer(_commandBuffer, &cbbi), "vkBeginCommandBuffer");

        if (clearedOnHost)
        {
            var hostToShader = new VulkanNative.VkMemoryBarrier
            {
                SType = VulkanNative.VkStructureTypeMemoryBarrier,
                SrcAccessMask = VulkanNative.VkAccessHostWriteBit,
                DstAccessMask = VulkanNative.VkAccessShaderReadBit | VulkanNative.VkAccessShaderWriteBit
            };
            VulkanNative.vkCmdPipelineBarrier(
                _commandBuffer,
                VulkanNative.VkPipelineStageHostBit,
                VulkanNative.VkPipelineStageComputeShaderBit,
                0,
                1, &hostToShader,
                0, 0, 0, 0);
        }

        VulkanNative.vkCmdBindPipeline(_commandBuffer, VulkanNative.VkPipelineBindPointCompute, _pipeline);
        ulong dset = _descriptorSet;
        VulkanNative.vkCmdBindDescriptorSets(_commandBuffer, VulkanNative.VkPipelineBindPointCompute, _pipelineLayout, 0, 1, &dset, 0, null);

        uint* pushConstants = stackalloc uint[PushConstantUints];
        pushConstants[0] = startTimestamp;
        pushConstants[1] = timestampCount;
        pushConstants[2] = plan.LowMask;
        pushConstants[3] = plan.LowTarget;
        pushConstants[4] = plan.HighMask;
        pushConstants[5] = plan.HighTarget;
        pushConstants[6] = (uint)capacity;
        pushConstants[7] = totalThreads;
        pushConstants[8] = (uint)resultMode;
        VulkanNative.vkCmdPushConstants(
            _commandBuffer,
            _pipelineLayout,
            VulkanNative.VkShaderStageComputeBit,
            0,
            PushConstantUints * sizeof(uint),
            pushConstants);

        VulkanNative.vkCmdDispatch(_commandBuffer, dispatchGroups, 1, 1);

        var shaderToHost = new VulkanNative.VkMemoryBarrier
        {
            SType = VulkanNative.VkStructureTypeMemoryBarrier,
            SrcAccessMask = VulkanNative.VkAccessShaderWriteBit,
            DstAccessMask = VulkanNative.VkAccessHostReadBit
        };
        VulkanNative.vkCmdPipelineBarrier(
            _commandBuffer,
            VulkanNative.VkPipelineStageComputeShaderBit,
            VulkanNative.VkPipelineStageHostBit,
            0,
            1, &shaderToHost,
            0, 0, 0, 0);

        CheckVk(VulkanNative.vkEndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

        nint cb = _commandBuffer;
        var submitInfo = new VulkanNative.VkSubmitInfo
        {
            SType = VulkanNative.VkStructureTypeSubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = (nint)(&cb)
        };
        CheckVk(VulkanNative.vkQueueSubmit(_queue, 1, &submitInfo, _fence), "vkQueueSubmit");

        ulong fence = _fence;
        CheckVk(VulkanNative.vkWaitForFences(_device, 1, &fence, 1, ulong.MaxValue), "vkWaitForFences");
        CheckVk(VulkanNative.vkResetFences(_device, 1, &fence), "vkResetFences");

        if (!_ssboCoherent)
        {
            var invalidateRange = new VulkanNative.VkMappedMemoryRange
            {
                SType = VulkanNative.VkStructureTypeMappedMemoryRange,
                Memory = _ssboMemory,
                Offset = 0,
                Size = VulkanNative.VkWholeSize
            };
            CheckVk(VulkanNative.vkInvalidateMappedMemoryRanges(_device, 1, &invalidateRange), "vkInvalidateMappedMemoryRanges(ssbo)");
        }

        uint[] readback = _resultsHost!;
        new ReadOnlySpan<uint>((void*)_ssboMapped, totalUints).CopyTo(readback);

        _counterMayBeDirty = GpuSearchBatch.HasHits(readback, strideUints, FourKeyCount);
        return readback;
    }

    private void UploadKeySchedulesIfNeeded(IReadOnlyList<byte[]> publicKeys)
    {
        bool unchanged = _hasCachedKeys;
        if (unchanged)
        {
            for (int lane = 0; lane < FourKeyCount; lane++)
            {
                if (!publicKeys[lane].AsSpan().SequenceEqual(_cachedKeys[lane]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            return;
        }

        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            if (publicKeys[lane] is not { Length: Ed25519KeyGenerator.KeyByteLength })
            {
                throw new ArgumentException("Each Ed25519 public key must be 32 bytes.", nameof(publicKeys));
            }
        }

        _hasCachedKeys = false;
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            uint[] sched = Sha1ScheduleHelper.GetOrComputeFixedSchedule(publicKeys[lane]);
            for (int t = 0; t < Sha1ScheduleHelper.ScheduleRounds; t++)
            {
                _uboStaging[(t * FourKeyCount) + lane] = sched[t];
            }
        }

        _uboStaging.AsSpan().CopyTo(new Span<uint>((void*)_uboMapped, UboUints));
        if (!_uboCoherent)
        {
            var flushRange = new VulkanNative.VkMappedMemoryRange
            {
                SType = VulkanNative.VkStructureTypeMappedMemoryRange,
                Memory = _uboMemory,
                Offset = 0,
                Size = VulkanNative.VkWholeSize
            };
            CheckVk(VulkanNative.vkFlushMappedMemoryRanges(_device, 1, &flushRange), "vkFlushMappedMemoryRanges(ubo)");
        }

        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            _cachedKeys[lane] = (byte[])publicKeys[lane].Clone();
        }

        _hasCachedKeys = true;
    }

}
