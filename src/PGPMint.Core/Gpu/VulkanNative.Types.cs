using System.Runtime.InteropServices;

namespace PGPMint.Core.Gpu;

internal static unsafe partial class VulkanNative
{
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
