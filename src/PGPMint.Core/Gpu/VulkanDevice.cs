using System.Runtime.InteropServices;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed unsafe class VulkanDevice : IGpuSearchDevice
{
    public const int FourKeyCount = 4;
    private const uint MaxDispatchGroupsCap = 65535u;
    private const int UboUints = (Sha1ScheduleHelper.ScheduleRounds + 1) * FourKeyCount; // 1,248 bytes
    private const ulong UboBytes = UboUints * sizeof(uint);
    private const int PushConstantUints = 9;

    private readonly VulkanAdapterDescriptor _adapter;
    private readonly nint _instance;
    private readonly nint _physicalDevice;
    private readonly nint _device;
    private readonly nint _queue;
    private readonly uint _workgroupSize;
    private readonly ulong _candidatesPerGroup;
    private readonly uint _maxDispatchGroupsX;

    private readonly ulong _descriptorSetLayout;
    private readonly ulong _pipelineLayout;
    private readonly ulong _shaderModule;
    private readonly ulong _pipelineCache;
    private readonly ulong _pipeline;
    private readonly ulong _descriptorPool;
    private readonly ulong _descriptorSet;
    private readonly ulong _commandPool;
    private readonly nint _commandBuffer;
    private readonly ulong _fence;

    private readonly ulong _uboBuffer;
    private readonly ulong _uboMemory;
    private readonly nint _uboMapped;
    private readonly bool _uboCoherent;

    private ulong _ssboBuffer;
    private ulong _ssboMemory;
    private nint _ssboMapped;
    private bool _ssboCoherent;
    private uint[]? _resultsHost;
    private int _allocatedCapacity;
    private bool _counterMayBeDirty = true;

    private readonly byte[][] _cachedKeys = new byte[FourKeyCount][];
    private readonly uint[] _uboStaging = new uint[UboUints];
    private bool _hasCachedKeys;
    private bool _disposed;

    public GpuDeviceInfo Info { get; }
    public int KeysPerBatch => FourKeyCount;
    public long MaxCandidatesPerBatch { get; }
    public string KernelVersion => VulkanSpirvSource.KernelVersion;

    internal bool LoadedPipelineCache { get; }

    internal static GpuKernelConfiguration ConfigurationFor(uint workgroupSize) =>
        new(FourKeyCount, (int)workgroupSize, 1, "ubo-spirv-v4");

    internal VulkanDevice(VulkanAdapterDescriptor adapter, GpuDeviceInfo info, string cacheDirectory)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        ArgumentNullException.ThrowIfNull(info);

        Array.Copy(Sha1ScheduleHelper.PhaseConstants, 0, _uboStaging, Sha1ScheduleHelper.ScheduleRounds * FourKeyCount, 4);

        MaxCandidatesPerBatch = info.DedicatedVideoMemoryBytes >= 6UL * 1024UL * 1024UL * 1024UL
            ? (1L << 30)
            : info.DedicatedVideoMemoryBytes >= 4UL * 1024UL * 1024UL * 1024UL
                ? (1L << 29)
                : (1L << 27);

        uint clampedWg = Math.Min(
            VulkanSpirvSource.DefaultWorkgroupSize,
            Math.Min(adapter.MaxWorkgroupSizeX, adapter.MaxWorkgroupInvocations));
        _workgroupSize = Math.Max(1u, clampedWg);
        _maxDispatchGroupsX = Math.Clamp(adapter.MaxWorkgroupCountX, 1u, MaxDispatchGroupsCap);
        _candidatesPerGroup = (ulong)_workgroupSize * (info.IsDiscrete ? 32UL : 8UL);

        GpuKernelConfiguration config = ConfigurationFor(_workgroupSize);
        Info = info with
        {
            BackendName = "Vulkan",
            BackendDriverVersion = adapter.DriverVersion,
            KernelConfiguration = config,
            KernelVersion = VulkanSpirvSource.KernelVersion
        };

        nint appNamePtr = Marshal.StringToHGlobalAnsi("PGPMint");
        try
        {
            var appInfo = new VulkanNative.VkApplicationInfo
            {
                SType = VulkanNative.VkStructureTypeApplicationInfo,
                PApplicationName = appNamePtr,
                ApplicationVersion = 1,
                PEngineName = appNamePtr,
                EngineVersion = 1,
                ApiVersion = VulkanNative.VkApiVersion11
            };

            var ici = new VulkanNative.VkInstanceCreateInfo
            {
                SType = VulkanNative.VkStructureTypeInstanceCreateInfo,
                PApplicationInfo = (nint)(&appInfo)
            };

            nint inst = 0;
            CheckVk(VulkanNative.vkCreateInstance(&ici, 0, &inst), "vkCreateInstance");
            _instance = inst;
        }
        finally
        {
            Marshal.FreeHGlobal(appNamePtr);
        }

        try
        {
            _physicalDevice = FindMatchingPhysicalDevice(_instance, adapter);

            float priority = 1.0f;
            var qci = new VulkanNative.VkDeviceQueueCreateInfo
            {
                SType = VulkanNative.VkStructureTypeDeviceQueueCreateInfo,
                QueueFamilyIndex = adapter.ComputeQueueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = (nint)(&priority)
            };

            var dci = new VulkanNative.VkDeviceCreateInfo
            {
                SType = VulkanNative.VkStructureTypeDeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = (nint)(&qci)
            };

            nint dev = 0;
            CheckVk(VulkanNative.vkCreateDevice(_physicalDevice, &dci, 0, &dev), "vkCreateDevice");
            _device = dev;

            nint queue = 0;
            VulkanNative.vkGetDeviceQueue(_device, adapter.ComputeQueueFamilyIndex, 0, &queue);
            _queue = queue;

            // Descriptor set layout: binding 0 = UBO, binding 1 = SSBO
            var bindings = stackalloc VulkanNative.VkDescriptorSetLayoutBinding[2];
            bindings[0] = new VulkanNative.VkDescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = VulkanNative.VkDescriptorTypeUniformBuffer,
                DescriptorCount = 1,
                StageFlags = VulkanNative.VkShaderStageComputeBit
            };
            bindings[1] = new VulkanNative.VkDescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = VulkanNative.VkDescriptorTypeStorageBuffer,
                DescriptorCount = 1,
                StageFlags = VulkanNative.VkShaderStageComputeBit
            };

            var dslci = new VulkanNative.VkDescriptorSetLayoutCreateInfo
            {
                SType = VulkanNative.VkStructureTypeDescriptorSetLayoutCreateInfo,
                BindingCount = 2,
                PBindings = (nint)bindings
            };
            ulong dsl = 0;
            CheckVk(VulkanNative.vkCreateDescriptorSetLayout(_device, &dslci, 0, &dsl), "vkCreateDescriptorSetLayout");
            _descriptorSetLayout = dsl;

            var pcr = new VulkanNative.VkPushConstantRange
            {
                StageFlags = VulkanNative.VkShaderStageComputeBit,
                Offset = 0,
                Size = PushConstantUints * sizeof(uint)
            };
            var plci = new VulkanNative.VkPipelineLayoutCreateInfo
            {
                SType = VulkanNative.VkStructureTypePipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = (nint)(&dsl),
                PushConstantRangeCount = 1,
                PPushConstantRanges = (nint)(&pcr)
            };
            ulong pl = 0;
            CheckVk(VulkanNative.vkCreatePipelineLayout(_device, &plci, 0, &pl), "vkCreatePipelineLayout");
            _pipelineLayout = pl;

            (_shaderModule, _pipelineCache, _pipeline, LoadedPipelineCache) = VulkanPipelineCache.CreateComputePipeline(
                _device, Info, adapter, (int)_workgroupSize, _pipelineLayout, cacheDirectory);

            var poolSizes = stackalloc VulkanNative.VkDescriptorPoolSize[2];
            poolSizes[0] = new VulkanNative.VkDescriptorPoolSize { Type = VulkanNative.VkDescriptorTypeUniformBuffer, DescriptorCount = 1 };
            poolSizes[1] = new VulkanNative.VkDescriptorPoolSize { Type = VulkanNative.VkDescriptorTypeStorageBuffer, DescriptorCount = 1 };

            var dpci = new VulkanNative.VkDescriptorPoolCreateInfo
            {
                SType = VulkanNative.VkStructureTypeDescriptorPoolCreateInfo,
                MaxSets = 1,
                PoolSizeCount = 2,
                PPoolSizes = (nint)poolSizes
            };
            ulong dpool = 0;
            CheckVk(VulkanNative.vkCreateDescriptorPool(_device, &dpci, 0, &dpool), "vkCreateDescriptorPool");
            _descriptorPool = dpool;

            var dsai = new VulkanNative.VkDescriptorSetAllocateInfo
            {
                SType = VulkanNative.VkStructureTypeDescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = (nint)(&dsl)
            };
            ulong dset = 0;
            CheckVk(VulkanNative.vkAllocateDescriptorSets(_device, &dsai, &dset), "vkAllocateDescriptorSets");
            _descriptorSet = dset;

            (_uboBuffer, _uboMemory, _uboMapped, _uboCoherent) = CreateMappedBuffer(
                UboBytes, VulkanNative.VkBufferUsageUniformBufferBit, preferCached: false);

            var cpci = new VulkanNative.VkCommandPoolCreateInfo
            {
                SType = VulkanNative.VkStructureTypeCommandPoolCreateInfo,
                Flags = VulkanNative.VkCommandPoolCreateResetCommandBufferBit,
                QueueFamilyIndex = adapter.ComputeQueueFamilyIndex
            };
            ulong cpool = 0;
            CheckVk(VulkanNative.vkCreateCommandPool(_device, &cpci, 0, &cpool), "vkCreateCommandPool");
            _commandPool = cpool;

            var cbai = new VulkanNative.VkCommandBufferAllocateInfo
            {
                SType = VulkanNative.VkStructureTypeCommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = 0,
                CommandBufferCount = 1
            };
            nint cb = 0;
            CheckVk(VulkanNative.vkAllocateCommandBuffers(_device, &cbai, &cb), "vkAllocateCommandBuffers");
            _commandBuffer = cb;

            var fci = new VulkanNative.VkFenceCreateInfo
            {
                SType = VulkanNative.VkStructureTypeFenceCreateInfo
            };
            ulong fence = 0;
            CheckVk(VulkanNative.vkCreateFence(_device, &fci, 0, &fence), "vkCreateFence");
            _fence = fence;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

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
            byte[] key = publicKeys[lane];
            if (key.Length != Ed25519KeyGenerator.KeyByteLength)
            {
                throw new ArgumentException("Each Ed25519 public key must be 32 bytes.", nameof(publicKeys));
            }

            uint[] sched = Sha1ScheduleHelper.GetOrComputeFixedSchedule(key);
            for (int t = 0; t < Sha1ScheduleHelper.ScheduleRounds; t++)
            {
                _uboStaging[(t * FourKeyCount) + lane] = sched[t];
            }

            _cachedKeys[lane] = (byte[])key.Clone();
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

        _hasCachedKeys = true;
    }

    private void EnsureResultsBuffer(int capacity)
    {
        if (_ssboBuffer != 0 && _allocatedCapacity == capacity)
        {
            return;
        }

        ReleaseResultsResources();

        int totalUints = FourKeyCount * (capacity + 1);
        ulong bytes = (ulong)(totalUints * sizeof(uint));

        (_ssboBuffer, _ssboMemory, _ssboMapped, _ssboCoherent) = CreateMappedBuffer(
            bytes, VulkanNative.VkBufferUsageStorageBufferBit, preferCached: true);

        var bufInfos = stackalloc VulkanNative.VkDescriptorBufferInfo[2];
        bufInfos[0] = new VulkanNative.VkDescriptorBufferInfo { Buffer = _uboBuffer, Offset = 0, Range = UboBytes };
        bufInfos[1] = new VulkanNative.VkDescriptorBufferInfo { Buffer = _ssboBuffer, Offset = 0, Range = bytes };

        var writes = stackalloc VulkanNative.VkWriteDescriptorSet[2];
        writes[0] = new VulkanNative.VkWriteDescriptorSet
        {
            SType = VulkanNative.VkStructureTypeWriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = VulkanNative.VkDescriptorTypeUniformBuffer,
            PBufferInfo = (nint)(&bufInfos[0])
        };
        writes[1] = new VulkanNative.VkWriteDescriptorSet
        {
            SType = VulkanNative.VkStructureTypeWriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = VulkanNative.VkDescriptorTypeStorageBuffer,
            PBufferInfo = (nint)(&bufInfos[1])
        };
        VulkanNative.vkUpdateDescriptorSets(_device, 2, writes, 0, 0);

        _resultsHost = new uint[totalUints];
        _allocatedCapacity = capacity;
        _counterMayBeDirty = true;
    }

    private (ulong Buffer, ulong Memory, nint MappedPtr, bool IsCoherent) CreateMappedBuffer(
        ulong sizeBytes,
        uint usage,
        bool preferCached)
    {
        var bci = new VulkanNative.VkBufferCreateInfo
        {
            SType = VulkanNative.VkStructureTypeBufferCreateInfo,
            Size = sizeBytes,
            Usage = usage,
            SharingMode = 0
        };

        ulong buffer = 0;
        CheckVk(VulkanNative.vkCreateBuffer(_device, &bci, 0, &buffer), "vkCreateBuffer");

        ulong memory = 0;
        bool mapped = false;
        try
        {
            VulkanNative.VkMemoryRequirements req;
            VulkanNative.vkGetBufferMemoryRequirements(_device, buffer, &req);

            VulkanNative.VkPhysicalDeviceMemoryProperties memProps;
            VulkanNative.vkGetPhysicalDeviceMemoryProperties(_physicalDevice, &memProps);

            (uint typeIndex, bool coherent) = SelectHostVisibleMemoryType(memProps, req.MemoryTypeBits, preferCached);

            var mai = new VulkanNative.VkMemoryAllocateInfo
            {
                SType = VulkanNative.VkStructureTypeMemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = typeIndex
            };

            CheckVk(VulkanNative.vkAllocateMemory(_device, &mai, 0, &memory), "vkAllocateMemory");
            CheckVk(VulkanNative.vkBindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");

            void* ptr = null;
            CheckVk(VulkanNative.vkMapMemory(_device, memory, 0, VulkanNative.VkWholeSize, 0, &ptr), "vkMapMemory");
            mapped = true;

            return (buffer, memory, (nint)ptr, coherent);
        }
        catch
        {
            if (mapped) VulkanNative.vkUnmapMemory(_device, memory);
            if (memory != 0) VulkanNative.vkFreeMemory(_device, memory, 0);
            if (buffer != 0) VulkanNative.vkDestroyBuffer(_device, buffer, 0);
            throw;
        }
    }

    private static (uint TypeIndex, bool IsCoherent) SelectHostVisibleMemoryType(
        VulkanNative.VkPhysicalDeviceMemoryProperties memProps,
        uint memoryTypeBits,
        bool preferCached)
    {
        uint hostVisibleCoherent = VulkanNative.VkMemoryPropertyHostVisibleBit | VulkanNative.VkMemoryPropertyHostCoherentBit;
        uint[] candidates = preferCached
            ? [hostVisibleCoherent | VulkanNative.VkMemoryPropertyHostCachedBit, hostVisibleCoherent, VulkanNative.VkMemoryPropertyHostVisibleBit]
            : [hostVisibleCoherent, VulkanNative.VkMemoryPropertyHostVisibleBit];

        foreach (uint desired in candidates)
        {
            for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            {
                if ((memoryTypeBits & (1u << (int)i)) == 0)
                {
                    continue;
                }

                uint flags = memProps.MemoryTypes[(int)(i * 2)];
                if ((flags & desired) == desired)
                {
                    return (i, (flags & VulkanNative.VkMemoryPropertyHostCoherentBit) != 0);
                }
            }
        }

        throw new InvalidOperationException("No HOST_VISIBLE Vulkan memory type available for buffer.");
    }

    private static nint FindMatchingPhysicalDevice(nint instance, VulkanAdapterDescriptor target)
    {
        uint count = 0;
        CheckVk(VulkanNative.vkEnumeratePhysicalDevices(instance, &count, null), "vkEnumeratePhysicalDevices(count)");
        if (count == 0)
        {
            throw new GpuDeviceLostException($"Vulkan GPU '{target.Name}' is no longer available.");
        }

        nint[] physicalDevices = new nint[count];
        fixed (nint* pDevs = physicalDevices)
        {
            CheckVk(VulkanNative.vkEnumeratePhysicalDevices(instance, &count, pDevs), "vkEnumeratePhysicalDevices");
        }

        foreach (nint phys in physicalDevices)
        {
            VulkanAdapterDescriptor? candidate = VulkanEnumerator.InspectPhysicalDevice(phys, VulkanNative.VkApiVersion11);
            if (candidate is null)
            {
                continue;
            }

            if (target.Luid.HasValue && candidate.Luid == target.Luid)
            {
                return phys;
            }

            if (candidate.VendorId == target.VendorId &&
                candidate.DeviceId == target.DeviceId &&
                string.Equals(candidate.Name, target.Name, StringComparison.Ordinal))
            {
                return phys;
            }
        }

        throw new GpuDeviceLostException($"Vulkan GPU '{target.Name}' could not be reopened.");
    }

    private void ReleaseResultsResources()
    {
        if (_ssboMapped != 0)
        {
            VulkanNative.vkUnmapMemory(_device, _ssboMemory);
            _ssboMapped = 0;
        }
        if (_ssboBuffer != 0)
        {
            VulkanNative.vkDestroyBuffer(_device, _ssboBuffer, 0);
            _ssboBuffer = 0;
        }
        if (_ssboMemory != 0)
        {
            VulkanNative.vkFreeMemory(_device, _ssboMemory, 0);
            _ssboMemory = 0;
        }
        _resultsHost = null;
        _allocatedCapacity = 0;
    }

    private static void CheckVk(int result, string operation)
    {
        if (result == VulkanNative.VkErrorDeviceLost)
        {
            throw new GpuDeviceLostException($"Vulkan {operation} failed because the GPU device was lost ({result}).");
        }

        if (result < VulkanNative.VkSuccess)
        {
            throw new InvalidOperationException($"Vulkan {operation} failed with error code {result}.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_fence != 0) { VulkanNative.vkDestroyFence(_device, _fence, 0); }
        if (_commandPool != 0) { VulkanNative.vkDestroyCommandPool(_device, _commandPool, 0); }
        if (_descriptorPool != 0) { VulkanNative.vkDestroyDescriptorPool(_device, _descriptorPool, 0); }
        ReleaseResultsResources();
        if (_uboMapped != 0) { VulkanNative.vkUnmapMemory(_device, _uboMemory); }
        if (_uboBuffer != 0) { VulkanNative.vkDestroyBuffer(_device, _uboBuffer, 0); }
        if (_uboMemory != 0) { VulkanNative.vkFreeMemory(_device, _uboMemory, 0); }
        if (_pipeline != 0) { VulkanNative.vkDestroyPipeline(_device, _pipeline, 0); }
        if (_pipelineLayout != 0) { VulkanNative.vkDestroyPipelineLayout(_device, _pipelineLayout, 0); }
        if (_descriptorSetLayout != 0) { VulkanNative.vkDestroyDescriptorSetLayout(_device, _descriptorSetLayout, 0); }
        if (_pipelineCache != 0) { VulkanNative.vkDestroyPipelineCache(_device, _pipelineCache, 0); }
        if (_shaderModule != 0) { VulkanNative.vkDestroyShaderModule(_device, _shaderModule, 0); }
        if (_device != 0) { VulkanNative.vkDestroyDevice(_device, 0); }
        if (_instance != 0) { VulkanNative.vkDestroyInstance(_instance, 0); }
    }
}
