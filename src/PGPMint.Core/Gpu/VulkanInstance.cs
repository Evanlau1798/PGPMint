using System.Runtime.InteropServices;

namespace PGPMint.Core.Gpu;

internal static unsafe class VulkanInstance
{
    internal const string PortabilityEnumeration = "VK_KHR_portability_enumeration";
    internal const string PortabilitySubset = "VK_KHR_portability_subset";
    internal static uint InstanceFlags(IEnumerable<string> extensions) => extensions.Contains(PortabilityEnumeration) ? 1u : 0;
    internal static string[] DeviceExtensions(IEnumerable<string> extensions) =>
        extensions.Contains(PortabilitySubset) ? [PortabilitySubset] : [];

    internal static nint Create(out uint apiVersion)
    {
        uint supported = 0;
        Check(VulkanNative.vkEnumerateInstanceVersion(&supported), "vkEnumerateInstanceVersion");
        if (supported < VulkanNative.VkApiVersion11)
            throw new NotSupportedException("Vulkan 1.1 or newer is required.");
        apiVersion = Math.Min(supported, VulkanNative.VkApiVersion12);
        string[] extensions = Extensions();
        uint flags = InstanceFlags(extensions);
        nint appName = Marshal.StringToCoTaskMemUTF8("PGPMint");
        nint extensionName = flags == 0 ? 0 : Marshal.StringToCoTaskMemUTF8(PortabilityEnumeration);
        try
        {
            var app = new VulkanNative.VkApplicationInfo
            {
                SType = VulkanNative.VkStructureTypeApplicationInfo, PApplicationName = appName,
                ApplicationVersion = 1, PEngineName = appName, EngineVersion = 1, ApiVersion = apiVersion
            };
            var info = new VulkanNative.VkInstanceCreateInfo
            {
                SType = VulkanNative.VkStructureTypeInstanceCreateInfo, PApplicationInfo = (nint)(&app),
                Flags = flags, EnabledExtensionCount = flags == 0 ? 0u : 1u,
                PpEnabledExtensionNames = flags == 0 ? 0 : (nint)(&extensionName)
            };
            nint instance = 0;
            Check(VulkanNative.vkCreateInstance(&info, 0, &instance), "vkCreateInstance");
            return instance;
        }
        finally
        {
            Marshal.FreeCoTaskMem(extensionName);
            Marshal.FreeCoTaskMem(appName);
        }
    }

    internal static string[] Extensions(nint physicalDevice = 0)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            Check(Enumerate(&count, null), "enumerate Vulkan extensions(count)");
            if (count == 0) return [];
            var entries = new VulkanNative.VkExtensionProperties[count];
            fixed (VulkanNative.VkExtensionProperties* properties = entries)
            {
                int result = Enumerate(&count, properties);
                if (result == 5) continue; // VK_INCOMPLETE: the loader's list changed.
                Check(result, "enumerate Vulkan extensions");
                var names = new string[count];
                for (int i = 0; i < count; i++)
                    names[i] = Marshal.PtrToStringUTF8((nint)properties[i].ExtensionName) ?? string.Empty;
                return names;
            }
        }
        throw new InvalidOperationException("Vulkan extension enumeration did not stabilize.");

        int Enumerate(uint* count, VulkanNative.VkExtensionProperties* entries) => physicalDevice == 0
            ? VulkanNative.vkEnumerateInstanceExtensionProperties(null, count, entries)
            : VulkanNative.vkEnumerateDeviceExtensionProperties(physicalDevice, null, count, entries);
    }

    internal static nint CreateDevice(nint physicalDevice, VulkanNative.VkDeviceQueueCreateInfo* queue)
    {
        bool portability = DeviceExtensions(Extensions(physicalDevice)).Length != 0;
        nint extension = portability ? Marshal.StringToCoTaskMemUTF8(PortabilitySubset) : 0;
        try
        {
            var info = new VulkanNative.VkDeviceCreateInfo
            {
                SType = VulkanNative.VkStructureTypeDeviceCreateInfo, QueueCreateInfoCount = 1,
                PQueueCreateInfos = (nint)queue, EnabledExtensionCount = portability ? 1u : 0u,
                PpEnabledExtensionNames = portability ? (nint)(&extension) : 0
            };
            nint device = 0;
            Check(VulkanNative.vkCreateDevice(physicalDevice, &info, 0, &device), "vkCreateDevice");
            return device;
        }
        finally { Marshal.FreeCoTaskMem(extension); }
    }

    internal static nint[] PhysicalDevices(nint instance)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            Check(VulkanNative.vkEnumeratePhysicalDevices(instance, &count, null), "vkEnumeratePhysicalDevices(count)");
            if (count == 0) return [];
            var devices = new nint[count];
            fixed (nint* pointer = devices)
            {
                int result = VulkanNative.vkEnumeratePhysicalDevices(instance, &count, pointer);
                if (result == 5) continue;
                Check(result, "vkEnumeratePhysicalDevices");
                return devices[..(int)count];
            }
        }
        throw new InvalidOperationException("Vulkan physical-device enumeration did not stabilize.");
    }

    private static void Check(int result, string operation)
    {
        if (result == VulkanNative.VkErrorDeviceLost)
            throw new PGPMint.Core.Search.GpuDeviceLostException($"{operation}: GPU device was lost ({result}).");
        if (result < 0) throw new InvalidOperationException($"{operation} failed ({result}).");
    }
}
