using System.Runtime.InteropServices;

namespace PGPMint.Core.Gpu;

internal static unsafe class VulkanNative
{
    public const int VkSuccess = 0;
    public const int VkErrorDeviceLost = -4;

    public const uint VkStructureTypeApplicationInfo = 0;
    public const uint VkStructureTypeInstanceCreateInfo = 1;
    public const uint VkStructureTypeDeviceQueueCreateInfo = 2;
    public const uint VkStructureTypeDeviceCreateInfo = 3;
    public const uint VkStructureTypeSubmitInfo = 4;
    public const uint VkStructureTypeMemoryAllocateInfo = 5;
    public const uint VkStructureTypeMappedMemoryRange = 6;
    public const uint VkStructureTypeFenceCreateInfo = 8;
    public const uint VkStructureTypeBufferCreateInfo = 12;
    public const uint VkStructureTypeShaderModuleCreateInfo = 16;
    public const uint VkStructureTypePipelineCacheCreateInfo = 17;
    public const uint VkStructureTypePipelineShaderStageCreateInfo = 18;
    public const uint VkStructureTypeComputePipelineCreateInfo = 29;
    public const uint VkStructureTypePipelineLayoutCreateInfo = 30;
    public const uint VkStructureTypeDescriptorSetLayoutCreateInfo = 32;
    public const uint VkStructureTypeDescriptorPoolCreateInfo = 33;
    public const uint VkStructureTypeDescriptorSetAllocateInfo = 34;
    public const uint VkStructureTypeWriteDescriptorSet = 35;
    public const uint VkStructureTypeCommandPoolCreateInfo = 39;
    public const uint VkStructureTypeCommandBufferAllocateInfo = 40;
    public const uint VkStructureTypeCommandBufferBeginInfo = 42;
    public const uint VkStructureTypeMemoryBarrier = 46;
    public const uint VkStructureTypePhysicalDeviceProperties2 = 1000059001;
    public const uint VkStructureTypePhysicalDeviceIdProperties = 1000071004;
    public const uint VkStructureTypePhysicalDeviceDriverProperties = 1000196000;

    public const uint VkApiVersion11 = (1u << 22) | (1u << 12);
    public const uint VkApiVersion12 = (1u << 22) | (2u << 12);

    public const uint VkPhysicalDeviceTypeCpu = 4;
    public const uint VkQueueComputeBit = 0x00000002u;

    public const uint VkBufferUsageUniformBufferBit = 0x00000010u;
    public const uint VkBufferUsageStorageBufferBit = 0x00000020u;

    public const uint VkMemoryPropertyHostVisibleBit = 0x00000002u;
    public const uint VkMemoryPropertyHostCoherentBit = 0x00000004u;
    public const uint VkMemoryPropertyHostCachedBit = 0x00000008u;

    public const uint VkDescriptorTypeUniformBuffer = 6;
    public const uint VkDescriptorTypeStorageBuffer = 7;

    public const uint VkShaderStageComputeBit = 0x00000020u;
    public const uint VkPipelineBindPointCompute = 1;

    public const uint VkCommandPoolCreateResetCommandBufferBit = 0x00000002u;
    public const uint VkCommandBufferUsageOneTimeSubmitBit = 0x00000001u;

    public const uint VkPipelineStageComputeShaderBit = 0x00000800u;
    public const uint VkPipelineStageHostBit = 0x00004000u;

    public const uint VkAccessShaderReadBit = 0x00000020u;
    public const uint VkAccessShaderWriteBit = 0x00000040u;
    public const uint VkAccessHostReadBit = 0x00002000u;
    public const uint VkAccessHostWriteBit = 0x00004000u;

    public const ulong VkWholeSize = ulong.MaxValue;

    private static readonly Lazy<nint> LibraryHandle = new(() =>
        NativeLibrary.TryLoad("vulkan-1.dll", out nint handle) ? handle : 0);

    private static readonly Lazy<bool> HasVulkan11Api = new(() =>
        LibraryHandle.Value != 0 &&
        NativeLibrary.TryGetExport(LibraryHandle.Value, "vkEnumerateInstanceVersion", out _) &&
        NativeLibrary.TryGetExport(LibraryHandle.Value, "vkGetPhysicalDeviceProperties2", out _));

    public static bool IsAvailable() => HasVulkan11Api.Value;

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkEnumerateInstanceVersion(uint* pApiVersion);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateInstance(VkInstanceCreateInfo* pCreateInfo, nint pAllocator, nint* pInstance);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyInstance(nint instance, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkEnumeratePhysicalDevices(nint instance, uint* pPhysicalDeviceCount, nint* pPhysicalDevices);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkGetPhysicalDeviceProperties2(nint physicalDevice, VkPhysicalDeviceProperties2* pProperties);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkGetPhysicalDeviceQueueFamilyProperties(nint physicalDevice, uint* pQueueFamilyPropertyCount, VkQueueFamilyProperties* pQueueFamilyProperties);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkGetPhysicalDeviceMemoryProperties(nint physicalDevice, VkPhysicalDeviceMemoryProperties* pMemoryProperties);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateDevice(nint physicalDevice, VkDeviceCreateInfo* pCreateInfo, nint pAllocator, nint* pDevice);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyDevice(nint device, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkGetDeviceQueue(nint device, uint queueFamilyIndex, uint queueIndex, nint* pQueue);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateShaderModule(nint device, VkShaderModuleCreateInfo* pCreateInfo, nint pAllocator, ulong* pShaderModule);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyShaderModule(nint device, ulong shaderModule, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreatePipelineCache(nint device, VkPipelineCacheCreateInfo* pCreateInfo, nint pAllocator, ulong* pPipelineCache);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyPipelineCache(nint device, ulong pipelineCache, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkGetPipelineCacheData(nint device, ulong pipelineCache, nuint* pDataSize, void* pData);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateDescriptorSetLayout(nint device, VkDescriptorSetLayoutCreateInfo* pCreateInfo, nint pAllocator, ulong* pSetLayout);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyDescriptorSetLayout(nint device, ulong descriptorSetLayout, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreatePipelineLayout(nint device, VkPipelineLayoutCreateInfo* pCreateInfo, nint pAllocator, ulong* pPipelineLayout);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyPipelineLayout(nint device, ulong pipelineLayout, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateComputePipelines(nint device, ulong pipelineCache, uint createInfoCount, VkComputePipelineCreateInfo* pCreateInfos, nint pAllocator, ulong* pPipelines);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyPipeline(nint device, ulong pipeline, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateBuffer(nint device, VkBufferCreateInfo* pCreateInfo, nint pAllocator, ulong* pBuffer);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyBuffer(nint device, ulong buffer, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkGetBufferMemoryRequirements(nint device, ulong buffer, VkMemoryRequirements* pMemoryRequirements);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkAllocateMemory(nint device, VkMemoryAllocateInfo* pAllocateInfo, nint pAllocator, ulong* pMemory);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkFreeMemory(nint device, ulong memory, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkBindBufferMemory(nint device, ulong buffer, ulong memory, ulong memoryOffset);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkMapMemory(nint device, ulong memory, ulong offset, ulong size, uint flags, void** ppData);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkUnmapMemory(nint device, ulong memory);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkFlushMappedMemoryRanges(nint device, uint memoryRangeCount, VkMappedMemoryRange* pMemoryRanges);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkInvalidateMappedMemoryRanges(nint device, uint memoryRangeCount, VkMappedMemoryRange* pMemoryRanges);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateDescriptorPool(nint device, VkDescriptorPoolCreateInfo* pCreateInfo, nint pAllocator, ulong* pDescriptorPool);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyDescriptorPool(nint device, ulong descriptorPool, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkAllocateDescriptorSets(nint device, VkDescriptorSetAllocateInfo* pAllocateInfo, ulong* pDescriptorSets);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkUpdateDescriptorSets(nint device, uint descriptorWriteCount, VkWriteDescriptorSet* pDescriptorWrites, uint descriptorCopyCount, nint pDescriptorCopies);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateCommandPool(nint device, VkCommandPoolCreateInfo* pCreateInfo, nint pAllocator, ulong* pCommandPool);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyCommandPool(nint device, ulong commandPool, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkAllocateCommandBuffers(nint device, VkCommandBufferAllocateInfo* pAllocateInfo, nint* pCommandBuffers);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkBeginCommandBuffer(nint commandBuffer, VkCommandBufferBeginInfo* pBeginInfo);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkEndCommandBuffer(nint commandBuffer);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkCmdBindPipeline(nint commandBuffer, uint pipelineBindPoint, ulong pipeline);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkCmdBindDescriptorSets(nint commandBuffer, uint pipelineBindPoint, ulong layout, uint firstSet, uint descriptorSetCount, ulong* pDescriptorSets, uint dynamicOffsetCount, uint* pDynamicOffsets);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkCmdPushConstants(nint commandBuffer, ulong layout, uint stageFlags, uint offset, uint size, void* pValues);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkCmdPipelineBarrier(
        nint commandBuffer,
        uint srcStageMask,
        uint dstStageMask,
        uint dependencyFlags,
        uint memoryBarrierCount,
        VkMemoryBarrier* pMemoryBarriers,
        uint bufferMemoryBarrierCount,
        nint pBufferMemoryBarriers,
        uint imageMemoryBarrierCount,
        nint pImageMemoryBarriers);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkCmdDispatch(nint commandBuffer, uint groupCountX, uint groupCountY, uint groupCountZ);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkCreateFence(nint device, VkFenceCreateInfo* pCreateInfo, nint pAllocator, ulong* pFence);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern void vkDestroyFence(nint device, ulong fence, nint pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkWaitForFences(nint device, uint fenceCount, ulong* pFences, uint waitAll, ulong timeout);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkResetFences(nint device, uint fenceCount, ulong* pFences);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkQueueSubmit(nint queue, uint submitCount, VkSubmitInfo* pSubmits, ulong fence);

    [StructLayout(LayoutKind.Sequential)]
    public struct VkApplicationInfo
    {
        public uint SType;
        public nint PNext;
        public nint PApplicationName;
        public uint ApplicationVersion;
        public nint PEngineName;
        public uint EngineVersion;
        public uint ApiVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkInstanceCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public nint PApplicationInfo;
        public uint EnabledLayerCount;
        public nint PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public nint PpEnabledExtensionNames;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct VkPhysicalDeviceIDProperties
    {
        [FieldOffset(0)] public uint SType;
        [FieldOffset(8)] public nint PNext;
        [FieldOffset(16)] public fixed byte DeviceUuid[16];
        [FieldOffset(32)] public fixed byte DriverUuid[16];
        [FieldOffset(48)] public fixed byte DeviceLuid[8];
        [FieldOffset(56)] public uint DeviceNodeMask;
        [FieldOffset(60)] public uint DeviceLuidValid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 536)]
    public struct VkPhysicalDeviceDriverProperties
    {
        [FieldOffset(0)] public uint SType;
        [FieldOffset(8)] public nint PNext;
        [FieldOffset(16)] public uint DriverId;
        [FieldOffset(20)] public fixed byte DriverName[256];
        [FieldOffset(276)] public fixed byte DriverInfo[256];
        [FieldOffset(532)] public uint ConformanceVersion;
    }

    [StructLayout(LayoutKind.Explicit, Size = 824)]
    public struct VkPhysicalDeviceProperties
    {
        [FieldOffset(0)] public uint ApiVersion;
        [FieldOffset(4)] public uint DriverVersion;
        [FieldOffset(8)] public uint VendorId;
        [FieldOffset(12)] public uint DeviceId;
        [FieldOffset(16)] public uint DeviceType;
        [FieldOffset(20)] public fixed byte DeviceName[256];
        [FieldOffset(276)] public fixed byte PipelineCacheUuid[16];
        [FieldOffset(516)] public fixed uint MaxComputeWorkGroupCount[3];
        [FieldOffset(528)] public uint MaxComputeWorkGroupInvocations;
        [FieldOffset(532)] public fixed uint MaxComputeWorkGroupSize[3];
        [FieldOffset(792)] public ulong NonCoherentAtomSize;
    }

    [StructLayout(LayoutKind.Explicit, Size = 840)]
    public struct VkPhysicalDeviceProperties2
    {
        [FieldOffset(0)] public uint SType;
        [FieldOffset(8)] public nint PNext;
        [FieldOffset(16)] public VkPhysicalDeviceProperties Properties;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkQueueFamilyProperties
    {
        public uint QueueFlags;
        public uint QueueCount;
        public uint TimestampValidBits;
        public fixed uint MinImageTransferGranularity[3];
    }

    [StructLayout(LayoutKind.Explicit, Size = 520)]
    public struct VkPhysicalDeviceMemoryProperties
    {
        [FieldOffset(0)] public uint MemoryTypeCount;
        [FieldOffset(4)] public fixed uint MemoryTypes[64]; // (propertyFlags, heapIndex) x 32
        [FieldOffset(260)] public uint MemoryHeapCount;
        [FieldOffset(264)] public fixed ulong MemoryHeaps[32]; // (size, flags+pad) x 16
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceQueueCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public uint QueueFamilyIndex;
        public uint QueueCount;
        public nint PQueuePriorities;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDeviceCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public uint QueueCreateInfoCount;
        public nint PQueueCreateInfos;
        public uint EnabledLayerCount;
        public nint PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public nint PpEnabledExtensionNames;
        public nint PEnabledFeatures;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkShaderModuleCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public nuint CodeSize;
        public nint PCode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPipelineCacheCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public nuint InitialDataSize;
        public nint PInitialData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDescriptorSetLayoutBinding
    {
        public uint Binding;
        public uint DescriptorType;
        public uint DescriptorCount;
        public uint StageFlags;
        public nint PImmutableSamplers;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDescriptorSetLayoutCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public uint BindingCount;
        public nint PBindings;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPushConstantRange
    {
        public uint StageFlags;
        public uint Offset;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPipelineLayoutCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public uint SetLayoutCount;
        public nint PSetLayouts;
        public uint PushConstantRangeCount;
        public nint PPushConstantRanges;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPipelineShaderStageCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public uint Stage;
        public ulong Module;
        public nint PName;
        public nint PSpecializationInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkComputePipelineCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public VkPipelineShaderStageCreateInfo Stage;
        public ulong Layout;
        public ulong BasePipelineHandle;
        public int BasePipelineIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkBufferCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public ulong Size;
        public uint Usage;
        public uint SharingMode;
        public uint QueueFamilyIndexCount;
        public nint PQueueFamilyIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryRequirements
    {
        public ulong Size;
        public ulong Alignment;
        public uint MemoryTypeBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryAllocateInfo
    {
        public uint SType;
        public nint PNext;
        public ulong AllocationSize;
        public uint MemoryTypeIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMappedMemoryRange
    {
        public uint SType;
        public nint PNext;
        public ulong Memory;
        public ulong Offset;
        public ulong Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDescriptorPoolSize
    {
        public uint Type;
        public uint DescriptorCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDescriptorPoolCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public uint MaxSets;
        public uint PoolSizeCount;
        public nint PPoolSizes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDescriptorSetAllocateInfo
    {
        public uint SType;
        public nint PNext;
        public ulong DescriptorPool;
        public uint DescriptorSetCount;
        public nint PSetLayouts;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkDescriptorBufferInfo
    {
        public ulong Buffer;
        public ulong Offset;
        public ulong Range;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkWriteDescriptorSet
    {
        public uint SType;
        public nint PNext;
        public ulong DstSet;
        public uint DstBinding;
        public uint DstArrayElement;
        public uint DescriptorCount;
        public uint DescriptorType;
        public nint PImageInfo;
        public nint PBufferInfo;
        public nint PTexelBufferView;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkCommandPoolCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public uint QueueFamilyIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkCommandBufferAllocateInfo
    {
        public uint SType;
        public nint PNext;
        public ulong CommandPool;
        public uint Level;
        public uint CommandBufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkCommandBufferBeginInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
        public nint PInheritanceInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkMemoryBarrier
    {
        public uint SType;
        public nint PNext;
        public uint SrcAccessMask;
        public uint DstAccessMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkSubmitInfo
    {
        public uint SType;
        public nint PNext;
        public uint WaitSemaphoreCount;
        public nint PWaitSemaphores;
        public nint PWaitDstStageMask;
        public uint CommandBufferCount;
        public nint PCommandBuffers;
        public uint SignalSemaphoreCount;
        public nint PSignalSemaphores;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkFenceCreateInfo
    {
        public uint SType;
        public nint PNext;
        public uint Flags;
    }
}
