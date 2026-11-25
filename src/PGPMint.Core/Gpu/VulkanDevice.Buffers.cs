using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed unsafe partial class VulkanDevice
{
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

}
