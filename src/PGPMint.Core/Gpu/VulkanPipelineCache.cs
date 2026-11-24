using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Gpu;

internal static unsafe class VulkanPipelineCache
{
    private const int MaxCacheBytes = 16 * 1024 * 1024;
    private const int DigestHexCharacters = 64;
    private const int MaxCacheCharacters = DigestHexCharacters + 1 + 4 * ((MaxCacheBytes + 2) / 3);
    private const int StandardCacheHeaderBytes = 32;
    private const uint VkPipelineCacheHeaderVersionOne = 1u;

    internal static (ulong ShaderModule, ulong PipelineCache, ulong Pipeline, bool LoadedCache) CreateComputePipeline(
        nint device,
        GpuDeviceInfo info,
        VulkanAdapterDescriptor adapter,
        int workgroupSize,
        ulong pipelineLayout,
        string cacheDirectory)
    {
        byte[] spirv = VulkanSpirvSource.BuildFourKeySpirv(workgroupSize);
        string cachePath = CachePath(cacheDirectory, info, adapter, workgroupSize, spirv);

        ulong shaderModule = 0;
        fixed (byte* pSpv = spirv)
        {
            var smci = new VulkanNative.VkShaderModuleCreateInfo
            {
                SType = VulkanNative.VkStructureTypeShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (nint)pSpv
            };

            int smErr = VulkanNative.vkCreateShaderModule(device, &smci, 0, &shaderModule);
            if (smErr != VulkanNative.VkSuccess || shaderModule == 0)
            {
                throw new InvalidOperationException($"vkCreateShaderModule failed ({smErr}).");
            }
        }

        nint entryNamePtr = Marshal.StringToHGlobalAnsi("main");
        ulong pipelineCache = 0;
        ulong pipeline = 0;
        try
        {
            byte[]? cachedData = ReadCache(cachePath, adapter);
            if (cachedData is not null &&
                TryBuildPipeline(device, shaderModule, entryNamePtr, pipelineLayout, cachedData, out pipelineCache, out pipeline))
            {
                return (shaderModule, pipelineCache, pipeline, true);
            }

            if (pipeline != 0)
            {
                VulkanNative.vkDestroyPipeline(device, pipeline, 0);
                pipeline = 0;
            }
            if (pipelineCache != 0)
            {
                VulkanNative.vkDestroyPipelineCache(device, pipelineCache, 0);
                pipelineCache = 0;
            }

            if (!TryBuildPipeline(device, shaderModule, entryNamePtr, pipelineLayout, initialData: null, out pipelineCache, out pipeline))
            {
                throw new InvalidOperationException("vkCreateComputePipelines failed.");
            }

            TryWriteCache(device, pipelineCache, cachePath);
            return (shaderModule, pipelineCache, pipeline, false);
        }
        catch
        {
            if (pipeline != 0) VulkanNative.vkDestroyPipeline(device, pipeline, 0);
            if (pipelineCache != 0) VulkanNative.vkDestroyPipelineCache(device, pipelineCache, 0);
            if (shaderModule != 0) VulkanNative.vkDestroyShaderModule(device, shaderModule, 0);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(entryNamePtr);
        }
    }

    private static bool TryBuildPipeline(
        nint device,
        ulong shaderModule,
        nint entryNamePtr,
        ulong pipelineLayout,
        byte[]? initialData,
        out ulong pipelineCache,
        out ulong pipeline)
    {
        pipelineCache = 0;
        pipeline = 0;

        ulong localCache = 0;
        int cacheResult;
        if (initialData is { Length: > 0 })
        {
            fixed (byte* pData = initialData)
            {
                var pcci = new VulkanNative.VkPipelineCacheCreateInfo
                {
                    SType = VulkanNative.VkStructureTypePipelineCacheCreateInfo,
                    InitialDataSize = (nuint)initialData.Length,
                    PInitialData = (nint)pData
                };
                cacheResult = VulkanNative.vkCreatePipelineCache(device, &pcci, 0, &localCache);
            }
        }
        else
        {
            var pcci = new VulkanNative.VkPipelineCacheCreateInfo
            {
                SType = VulkanNative.VkStructureTypePipelineCacheCreateInfo
            };
            cacheResult = VulkanNative.vkCreatePipelineCache(device, &pcci, 0, &localCache);
        }

        pipelineCache = localCache;
        if (cacheResult != VulkanNative.VkSuccess || localCache == 0)
        {
            return false;
        }

        var cpci = new VulkanNative.VkComputePipelineCreateInfo
        {
            SType = VulkanNative.VkStructureTypeComputePipelineCreateInfo,
            Stage = new VulkanNative.VkPipelineShaderStageCreateInfo
            {
                SType = VulkanNative.VkStructureTypePipelineShaderStageCreateInfo,
                Stage = VulkanNative.VkShaderStageComputeBit,
                Module = shaderModule,
                PName = entryNamePtr
            },
            Layout = pipelineLayout,
            BasePipelineIndex = -1
        };

        ulong localPipeline = 0;
        int pipeResult = VulkanNative.vkCreateComputePipelines(device, localCache, 1, &cpci, 0, &localPipeline);
        pipeline = localPipeline;
        return pipeResult == VulkanNative.VkSuccess && localPipeline != 0;
    }

    private static string CachePath(
        string directory,
        GpuDeviceInfo info,
        VulkanAdapterDescriptor adapter,
        int workgroupSize,
        byte[] spirv)
    {
        string uuidHex = Convert.ToHexString(adapter.PipelineCacheUuid);
        var identity = FormattableString.Invariant(
            $"v1|{RuntimeInformation.ProcessArchitecture}|{info.DeviceKey}|{info.Name}|{info.DriverVersion}|{adapter.DriverVersion}|{VulkanSpirvSource.KernelVersion}|{workgroupSize}|{uuidHex}|{Convert.ToHexString(SHA256.HashData(spirv))}");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(directory, $"{key}.vkcache");
    }

    private static byte[]? ReadCache(string path, VulkanAdapterDescriptor adapter)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long length = stream.Length;
            if (length <= DigestHexCharacters + 1 || length > MaxCacheCharacters)
            {
                return null;
            }

            var encoded = new byte[(int)length];
            stream.ReadExactly(encoded);
            if (encoded[DigestHexCharacters] != (byte)'\n')
            {
                return null;
            }

            string storedDigest = Encoding.ASCII.GetString(encoded, 0, DigestHexCharacters);
            byte[] binary = Convert.FromBase64String(Encoding.ASCII.GetString(encoded, DigestHexCharacters + 1,
                encoded.Length - DigestHexCharacters - 1));
            if (binary.Length is < StandardCacheHeaderBytes or > MaxCacheBytes)
            {
                return null;
            }

            if (!string.Equals(storedDigest, Convert.ToHexString(SHA256.HashData(binary)), StringComparison.Ordinal))
            {
                return null;
            }

            // Validate Vulkan standard pipeline cache header (Table 10.1) so stale/corrupt blobs trigger clean rebuild.
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(0, 4));
            uint headerVersion = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(4, 4));
            uint vendorId = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(8, 4));
            uint deviceId = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(12, 4));
            if (headerSize < StandardCacheHeaderBytes ||
                headerSize > (uint)binary.Length ||
                headerVersion != VkPipelineCacheHeaderVersionOne ||
                vendorId != adapter.VendorId ||
                deviceId != adapter.DeviceId ||
                !binary.AsSpan(16, 16).SequenceEqual(adapter.PipelineCacheUuid))
            {
                return null;
            }

            return binary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static void TryWriteCache(nint device, ulong pipelineCache, string path)
    {
        try
        {
            nuint size = 0;
            if (VulkanNative.vkGetPipelineCacheData(device, pipelineCache, &size, null) != VulkanNative.VkSuccess ||
                size == 0 || size > MaxCacheBytes)
            {
                return;
            }

            byte[] data = new byte[(int)size];
            fixed (byte* pData = data)
            {
                if (VulkanNative.vkGetPipelineCacheData(device, pipelineCache, &size, pData) != VulkanNative.VkSuccess)
                {
                    return;
                }
            }

            string digest = Convert.ToHexString(SHA256.HashData(data));
            AtomicFileStore.WriteTextAtomic(path, $"{digest}\n{Convert.ToBase64String(data)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache persistence is best-effort.
        }
    }
}
