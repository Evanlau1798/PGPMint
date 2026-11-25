using System.Runtime.InteropServices;
using System.Text;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

internal sealed record VulkanAdapterDescriptor(
    uint VendorId,
    uint DeviceId,
    string Name,
    string DriverVersion,
    uint ComputeQueueFamilyIndex,
    uint MaxWorkgroupSizeX,
    uint MaxWorkgroupInvocations,
    uint MaxWorkgroupCountX,
    ulong NonCoherentAtomSize,
    long? Luid,
    byte[] PipelineCacheUuid)
{
    public string? DeviceUuid { get; init; }
    public string? PciAddress { get; init; }
    public ulong? MetalRegistryId { get; init; }
    public ulong LocalMemoryBytes { get; init; }
    public bool IsDiscrete { get; init; }
    public bool RequiresPortabilitySubset { get; init; }

    internal GpuDeviceInfo ToDeviceInfo(int workgroupSize = 128) => new(
        GpuDeviceIdentity.Key(DeviceUuid, PciAddress, Luid), Luid ?? 0, Name, VendorId, DeviceId,
        0, 0, IsDiscrete ? LocalMemoryBytes : 0, DriverVersion, IsDiscrete, "Vulkan", VulkanSpirvSource.KernelVersion)
    {
        DeviceUuid = DeviceUuid,
        PciAddress = PciAddress,
        MetalRegistryId = MetalRegistryId,
        BackendDriverVersion = DriverVersion,
        KernelConfiguration = VulkanDevice.ConfigurationFor(Math.Min(MaxWorkgroupSizeX, MaxWorkgroupInvocations), workgroupSize)
    };
}

internal static unsafe class VulkanEnumerator
{
    public static IReadOnlyList<VulkanAdapterDescriptor> TryEnumerateGpuAdapters(Action<string>? report = null)
    {
        var results = new List<VulkanAdapterDescriptor>();
        if (!VulkanNative.IsAvailable())
        {
            report?.Invoke("Vulkan 1.1 loader is unavailable; install the platform Vulkan loader and a hardware GPU ICD.");
            return results;
        }
        try
        {
            nint instance = VulkanInstance.Create(out uint apiVersion);
            try
            {
                foreach (nint physical in VulkanInstance.PhysicalDevices(instance))
                {
                    var adapter = InspectPhysicalDevice(physical, apiVersion);
                    if (adapter is not null) results.Add(adapter);
                }
            }
            finally { VulkanNative.vkDestroyInstance(instance, 0); }
        }
        catch (Exception ex) { report?.Invoke($"Vulkan enumeration failed: {ex.Message}"); }
        return results;
    }

    internal static VulkanAdapterDescriptor? InspectPhysicalDevice(nint physicalDevice, uint instanceApiVersion)
    {
        var idProps = new VulkanNative.VkPhysicalDeviceIDProperties
        {
            SType = VulkanNative.VkStructureTypePhysicalDeviceIdProperties
        };

        var props2 = new VulkanNative.VkPhysicalDeviceProperties2
        {
            SType = VulkanNative.VkStructureTypePhysicalDeviceProperties2,
            PNext = (nint)(&idProps)
        };

        VulkanNative.vkGetPhysicalDeviceProperties2(physicalDevice, &props2);

        if (props2.Properties.DeviceType is not (1 or 2) ||
            props2.Properties.ApiVersion < VulkanNative.VkApiVersion11)
        {
            return null;
        }

        uint queueFamilyCount = 0;
        VulkanNative.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, null);
        if (queueFamilyCount == 0)
        {
            return null;
        }

        var queueFamilies = new VulkanNative.VkQueueFamilyProperties[queueFamilyCount];
        fixed (VulkanNative.VkQueueFamilyProperties* pQueues = queueFamilies)
        {
            VulkanNative.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, &queueFamilyCount, pQueues);
        }

        int? computeFamily = null;
        for (int i = 0; i < queueFamilies.Length; i++)
        {
            if (queueFamilies[i].QueueCount > 0 &&
                (queueFamilies[i].QueueFlags & VulkanNative.VkQueueComputeBit) != 0)
            {
                computeFamily = i;
                break;
            }
        }

        if (!computeFamily.HasValue)
        {
            return null;
        }

        string name = ReadFixedAscii(props2.Properties.DeviceName, 256).Trim();
        if (OpenClEnumerator.IsSoftware(name)) return null;
        string[] extensions = VulkanInstance.Extensions(physicalDevice);
        string? pciAddress = null;
        if (extensions.Contains("VK_EXT_pci_bus_info"))
        {
            var pci = new VulkanNative.VkPhysicalDevicePciBusInfoProperties { SType = 1000212000 };
            var pciProperties = new VulkanNative.VkPhysicalDeviceProperties2
            {
                SType = VulkanNative.VkStructureTypePhysicalDeviceProperties2, PNext = (nint)(&pci)
            };
            VulkanNative.vkGetPhysicalDeviceProperties2(physicalDevice, &pciProperties);
            pciAddress = $"{pci.PciDomain:x4}:{pci.PciBus:x2}:{pci.PciDevice:x2}.{pci.PciFunction:x}";
        }

        VulkanNative.VkPhysicalDeviceMemoryProperties memory;
        VulkanNative.vkGetPhysicalDeviceMemoryProperties(physicalDevice, &memory);
        ulong localMemory = 0;
        for (int i = 0; i < memory.MemoryHeapCount && i < 16; i++)
            if ((memory.MemoryHeaps[i * 2 + 1] & 1UL) != 0) localMemory += memory.MemoryHeaps[i * 2];
        long? luid = null;
        if (OperatingSystem.IsWindows() && idProps.DeviceLuidValid != 0)
        {
            uint low = *(uint*)idProps.DeviceLuid;
            int high = *(int*)(idProps.DeviceLuid + 4);
            luid = ((long)high << 32) | low;
        }

        byte[] uuid = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            uuid[i] = props2.Properties.PipelineCacheUuid[i];
        }

        string driverVersion = $"0x{props2.Properties.DriverVersion:X8}";
        if (instanceApiVersion >= VulkanNative.VkApiVersion12 &&
            props2.Properties.ApiVersion >= VulkanNative.VkApiVersion12)
        {
            var drvProps = new VulkanNative.VkPhysicalDeviceDriverProperties
            {
                SType = VulkanNative.VkStructureTypePhysicalDeviceDriverProperties
            };

            var props2Drv = new VulkanNative.VkPhysicalDeviceProperties2
            {
                SType = VulkanNative.VkStructureTypePhysicalDeviceProperties2,
                PNext = (nint)(&drvProps)
            };

            VulkanNative.vkGetPhysicalDeviceProperties2(physicalDevice, &props2Drv);
            string info = ReadFixedAscii(drvProps.DriverInfo, 256).Trim();
            if (!string.IsNullOrWhiteSpace(info))
            {
                driverVersion = info;
            }
        }

        return new VulkanAdapterDescriptor(
            VendorId: props2.Properties.VendorId,
            DeviceId: props2.Properties.DeviceId,
            Name: name,
            DriverVersion: driverVersion,
            ComputeQueueFamilyIndex: (uint)computeFamily.Value,
            MaxWorkgroupSizeX: props2.Properties.MaxComputeWorkGroupSize[0],
            MaxWorkgroupInvocations: props2.Properties.MaxComputeWorkGroupInvocations,
            MaxWorkgroupCountX: props2.Properties.MaxComputeWorkGroupCount[0],
            NonCoherentAtomSize: Math.Max(1UL, props2.Properties.NonCoherentAtomSize),
            Luid: luid,
            PipelineCacheUuid: uuid)
        {
            DeviceUuid = GpuDeviceIdentity.NormalizeUuid(new ReadOnlySpan<byte>(idProps.DeviceUuid, 16)),
            MetalRegistryId = GpuDeviceIdentity.MoltenVkRegistryId(new ReadOnlySpan<byte>(idProps.DeviceLuid, 8),
                new ReadOnlySpan<byte>(idProps.DriverUuid, 16), OperatingSystem.IsMacOS(), idProps.DeviceLuidValid != 0),
            PciAddress = pciAddress, LocalMemoryBytes = localMemory, IsDiscrete = props2.Properties.DeviceType == 2,
            RequiresPortabilitySubset = extensions.Contains(VulkanInstance.PortabilitySubset)
        };
    }

    private static string ReadFixedAscii(byte* buffer, int maxLength)
    {
        int len = 0;
        while (len < maxLength && buffer[len] != 0)
        {
            len++;
        }

        return len == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, len);
    }
}
