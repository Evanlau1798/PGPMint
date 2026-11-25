using System.Runtime.InteropServices;

namespace PGPMint.Core.Gpu;

internal static unsafe partial class VulkanNative
{
    static VulkanNative() => NativeGpuLibraries.Initialize();
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

    private static readonly Lazy<nint> LibraryHandle = new(() => NativeGpuLibraries.Handle("vulkan-1.dll"));

    private static readonly Lazy<bool> HasVulkan11Api = new(() =>
        LibraryHandle.Value != 0 &&
        NativeLibrary.TryGetExport(LibraryHandle.Value, "vkEnumerateInstanceVersion", out _) &&
        NativeLibrary.TryGetExport(LibraryHandle.Value, "vkGetPhysicalDeviceProperties2", out _));

    public static bool IsAvailable() => HasVulkan11Api.Value;

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkEnumerateInstanceExtensionProperties(byte* layer, uint* count, VkExtensionProperties* properties);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.Winapi)]
    public static extern int vkEnumerateDeviceExtensionProperties(nint device, byte* layer, uint* count, VkExtensionProperties* properties);

    [StructLayout(LayoutKind.Sequential)]
    public struct VkExtensionProperties
    {
        public fixed byte ExtensionName[256];
        public uint SpecVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VkPhysicalDevicePciBusInfoProperties
    {
        public uint SType;
        public nint PNext;
        public uint PciDomain;
        public uint PciBus;
        public uint PciDevice;
        public uint PciFunction;
    }

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

}
