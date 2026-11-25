using System.Runtime.InteropServices;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed unsafe partial class VulkanDevice : IGpuSearchDevice
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

    internal static GpuKernelConfiguration ConfigurationFor(uint maxWorkgroupSize, int workgroupSize = 128) =>
        new(FourKeyCount, (int)Math.Clamp(maxWorkgroupSize, 1, (uint)workgroupSize), 1, "ubo-spirv-v4");

    internal VulkanDevice(VulkanAdapterDescriptor adapter, GpuDeviceInfo info, string cacheDirectory, int workgroupSize = 128)
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
            (uint)GpuDeviceIdentity.ValidateWorkgroup(workgroupSize),
            Math.Min(adapter.MaxWorkgroupSizeX, adapter.MaxWorkgroupInvocations));
        _workgroupSize = Math.Max(1u, clampedWg);
        _maxDispatchGroupsX = Math.Clamp(adapter.MaxWorkgroupCountX, 1u, MaxDispatchGroupsCap);
        _candidatesPerGroup = (ulong)_workgroupSize * (info.IsDiscrete ? 32UL : 8UL);

        GpuKernelConfiguration config = ConfigurationFor(_workgroupSize, workgroupSize);
        Info = info with
        {
            BackendName = "Vulkan",
            BackendDriverVersion = adapter.DriverVersion,
            KernelConfiguration = config,
            KernelVersion = VulkanSpirvSource.KernelVersion
        };

        _instance = VulkanInstance.Create(out _);

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

            _device = VulkanInstance.CreateDevice(_physicalDevice, &qci);

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

            if (GpuDeviceIdentity.Same(candidate.ToDeviceInfo(), target.ToDeviceInfo()))
            {
                return phys;
            }
        }

        throw new GpuDeviceLostException($"Vulkan GPU '{target.Name}' could not be reopened.");
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
