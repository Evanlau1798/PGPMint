using System.Runtime.InteropServices;
using System.Reflection;

namespace PGPMint.Core.Gpu;

internal static class NativeGpuLibraries
{
    private static readonly Lazy<nint> Vulkan = new(() => Load("vulkan-1.dll"));
    private static readonly Lazy<nint> OpenCl = new(() => Load("OpenCL.dll"));

    static NativeGpuLibraries() => NativeLibrary.SetDllImportResolver(typeof(NativeGpuLibraries).Assembly, Resolve);

    internal static void Initialize() { }

    internal static nint Handle(string library) => library switch
    {
        "vulkan-1.dll" => Vulkan.Value,
        "OpenCL.dll" => OpenCl.Value,
        _ => 0
    };

    private static nint Resolve(string library, Assembly assembly, DllImportSearchPath? searchPath) => Handle(library);

    private static nint Load(string library)
    {
        OSPlatform platform = OperatingSystem.IsWindows() ? OSPlatform.Windows :
            OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        foreach (string candidate in Candidates(library, platform))
        {
            if (NativeLibrary.TryLoad(candidate, typeof(NativeGpuLibraries).Assembly, null, out nint handle)) return handle;
        }
        return 0;
    }

    internal static IReadOnlyList<string> Candidates(string library, OSPlatform platform)
    {
        if (library is not ("vulkan-1.dll" or "OpenCL.dll")) return [];
        if (platform == OSPlatform.Windows) return [library];
        if (platform == OSPlatform.Linux)
            return library == "vulkan-1.dll" ? ["libvulkan.so.1"] : ["libOpenCL.so.1"];
        if (platform != OSPlatform.OSX) return [];
        return library == "OpenCL.dll" ? ["/System/Library/Frameworks/OpenCL.framework/OpenCL"] :
        [
            "libvulkan.1.dylib", "/opt/homebrew/lib/libvulkan.1.dylib", "/usr/local/lib/libvulkan.1.dylib",
            "libMoltenVK.dylib", Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib"),
            "/opt/homebrew/lib/libMoltenVK.dylib", "/usr/local/lib/libMoltenVK.dylib",
            "/Library/Frameworks/MoltenVK.framework/MoltenVK"
        ];
    }
}
