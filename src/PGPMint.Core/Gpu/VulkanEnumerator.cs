using System.Runtime.InteropServices;
using System.Text;

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
    byte[] PipelineCacheUuid);

internal static unsafe class VulkanEnumerator
{
    public static IReadOnlyList<VulkanAdapterDescriptor> TryEnumerateGpuAdapters()
    {
        var results = new List<VulkanAdapterDescriptor>();
        if (!VulkanNative.IsAvailable())
        {
            return results;
        }

        try
        {
            uint instanceVersion = 0;
            if (VulkanNative.vkEnumerateInstanceVersion(&instanceVersion) != VulkanNative.VkSuccess ||
                instanceVersion < VulkanNative.VkApiVersion11)
            {
                return results;
            }

            uint targetApi = instanceVersion >= VulkanNative.VkApiVersion12
                ? VulkanNative.VkApiVersion12
                : VulkanNative.VkApiVersion11;

            nint appNamePtr = Marshal.StringToHGlobalAnsi("PGPMint");
            nint instance = 0;
            try
            {
                var appInfo = new VulkanNative.VkApplicationInfo
                {
                    SType = VulkanNative.VkStructureTypeApplicationInfo,
                    PApplicationName = appNamePtr,
                    ApplicationVersion = 1,
                    PEngineName = appNamePtr,
                    EngineVersion = 1,
                    ApiVersion = targetApi
                };

                var ici = new VulkanNative.VkInstanceCreateInfo
                {
                    SType = VulkanNative.VkStructureTypeInstanceCreateInfo,
                    PApplicationInfo = (nint)(&appInfo)
                };

                if (VulkanNative.vkCreateInstance(&ici, 0, &instance) != VulkanNative.VkSuccess || instance == 0)
                {
                    return results;
                }

                uint devCount = 0;
                if (VulkanNative.vkEnumeratePhysicalDevices(instance, &devCount, null) != VulkanNative.VkSuccess || devCount == 0)
                {
                    return results;
                }

                nint[] physicalDevices = new nint[devCount];
                fixed (nint* pDevs = physicalDevices)
                {
                    if (VulkanNative.vkEnumeratePhysicalDevices(instance, &devCount, pDevs) != VulkanNative.VkSuccess)
                    {
                        return results;
                    }
                }

                foreach (nint phys in physicalDevices)
                {
                    VulkanAdapterDescriptor? desc = InspectPhysicalDevice(phys, targetApi);
                    if (desc is not null)
                    {
                        results.Add(desc);
                    }
                }
            }
            finally
            {
                if (instance != 0)
                {
                    VulkanNative.vkDestroyInstance(instance, 0);
                }

                Marshal.FreeHGlobal(appNamePtr);
            }
        }
        catch
        {
            // Vulkan is an optional zero-dependency accelerator; fall back cleanly to DirectCompute on error.
        }

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

        if (props2.Properties.DeviceType == VulkanNative.VkPhysicalDeviceTypeCpu ||
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
        long? luid = null;
        if (idProps.DeviceLuidValid != 0)
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
            PipelineCacheUuid: uuid);
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
