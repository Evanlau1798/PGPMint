using System.Security.Cryptography;
using System.Text;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PGPMint.Core.Gpu;

public sealed class DirectComputeDevice : IGpuSearchDevice
{
    public const int FourKeyCount = 4;
    private const uint MaxDispatchGroups = 65535u;
    private const int ConstantBufferBytes = 32 + (Sha1ScheduleHelper.ScheduleRounds * 16); // 1264 bytes

    private static readonly object ShaderCompileLock = new();
    private static byte[]? _cachedShaderBytecode;

    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly uint[] _constantStaging = new uint[ConstantBufferBytes / 4];

    private ID3D11Buffer? _resultsBuffer;
    private ID3D11UnorderedAccessView? _resultsUav;
    private ID3D11Buffer? _resultsStaging;
    private int _allocatedCapacity;
    private bool _counterMayBeDirty = true;

    private readonly byte[][] _cachedKeys = new byte[FourKeyCount][];
    private bool _hasCachedKeys;
    private bool _disposed;

    public GpuDeviceInfo Info { get; }
    public int KeysPerBatch => FourKeyCount;
    public long MaxCandidatesPerBatch { get; }
    public string KernelVersion => DirectComputeShaderSource.KernelVersion;

    internal DirectComputeDevice(IDXGIAdapter1 adapter, GpuDeviceInfo info, string? shaderCacheDirectory)
    {
        _adapter = adapter;
        Info = info;

        // Larger batch cap for discrete GPUs with >= 4GB VRAM while staying safely under Windows 2s TDR
        MaxCandidatesPerBatch = info.DedicatedVideoMemoryBytes >= 4UL * 1024UL * 1024UL * 1024UL
            ? (1L << 29) // 536,870,912 candidates (~25ms on RTX 3080)
            : (1L << 27); // 134,217,728 candidates (safe for integrated / entry GPUs)

        FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var hr = D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.None,
            levels,
            out ID3D11Device? device,
            out FeatureLevel actualLevel,
            out ID3D11DeviceContext? context);

        if (hr.Failure || device is null || context is null || actualLevel < FeatureLevel.Level_11_0)
        {
            context?.Dispose();
            device?.Dispose();
            throw new InvalidOperationException($"Failed to initialize D3D11 compute device for '{info.Name}' (HRESULT 0x{hr.Code:X8}).");
        }

        _device = device;
        _context = context;

        try
        {
            _shader = CreateComputeShaderWithSelfHealingCache(shaderCacheDirectory);

            var cbDesc = new BufferDescription(
                byteWidth: (uint)ConstantBufferBytes,
                bindFlags: BindFlags.ConstantBuffer,
                usage: ResourceUsage.Dynamic,
                cpuAccessFlags: CpuAccessFlags.Write);
            _constantBuffer = _device.CreateBuffer(cbDesc);
        }
        catch
        {
            _constantBuffer?.Dispose();
            _shader?.Dispose();
            _context.Dispose();
            _device.Dispose();
            throw;
        }
    }

    public IReadOnlyList<uint>[] ScanGroup(
        IReadOnlyList<byte[]> publicKeys,
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
        int capacity = 1024) =>
        ScanBatch(publicKeys, startTimestamp, timestampCount, pattern, capacity).Hits;

    public SearchBatchResult ScanBatch(
        IReadOnlyList<byte[]> publicKeys,
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
        int capacity = 1024)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentNullException.ThrowIfNull(pattern);

        if (publicKeys.Count != FourKeyCount)
        {
            throw new ArgumentException($"Expected exactly {FourKeyCount} public keys.", nameof(publicKeys));
        }

        if (timestampCount == 0 || (ulong)startTimestamp + timestampCount > (1UL << 32))
        {
            throw new ArgumentOutOfRangeException(nameof(timestampCount), "Timestamp range exceeds uint32 bounds.");
        }

        if ((long)timestampCount * FourKeyCount > MaxCandidatesPerBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampCount), "Batch exceeds device MaxCandidatesPerBatch.");
        }

        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        UpdateKeySchedulesIfNeeded(publicKeys);
        EnsureResultsBuffers(capacity);

        long evaluations = 0;
        var hits = ScanGroupCore(startTimestamp, timestampCount, pattern, capacity, ref evaluations);
        return new SearchBatchResult(hits, (long)timestampCount * FourKeyCount, evaluations);
    }

    private IReadOnlyList<uint>[] ScanGroupCore(
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
        int capacity,
        ref long evaluations)
    {
        int strideUints = capacity + 1;
        int totalUints = FourKeyCount * strideUints;

        if (_counterMayBeDirty)
        {
            _context.ClearUnorderedAccessView(_resultsUav!, new Vortice.Mathematics.Int4(0, 0, 0, 0));
        }

        // Assume dirty until clean empty readback completes
        _counterMayBeDirty = true;

        uint candidatesPerGroup = (uint)DirectComputeShaderSource.ThreadGroupSize * (Info.IsDiscrete ? 32u : 8u);
        uint desiredGroups = (uint)(((ulong)timestampCount + candidatesPerGroup - 1UL) / candidatesPerGroup);
        uint dispatchGroups = Math.Clamp(desiredGroups, 1u, MaxDispatchGroups);
        uint totalThreads = dispatchGroups * (uint)DirectComputeShaderSource.ThreadGroupSize;

        _constantStaging[0] = startTimestamp;
        _constantStaging[1] = timestampCount;
        _constantStaging[2] = pattern.MaskLow;
        _constantStaging[3] = pattern.TargetLow;
        _constantStaging[4] = pattern.MaskHigh;
        _constantStaging[5] = pattern.TargetHigh;
        _constantStaging[6] = (uint)capacity;
        _constantStaging[7] = totalThreads;

        UploadConstantBuffer();

        _context.CSSetShader(_shader);
        _context.CSSetConstantBuffer(0, _constantBuffer);
        _context.CSSetUnorderedAccessView(0, _resultsUav!);
        _context.Dispatch(dispatchGroups, 1, 1);

        _context.CopyResource(_resultsStaging!, _resultsBuffer!);

        uint[] readback = new uint[totalUints];
        MappedSubresource mapped = _context.Map(_resultsStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                new ReadOnlySpan<uint>((void*)mapped.DataPointer, totalUints).CopyTo(readback);
            }
        }
        finally
        {
            _context.Unmap(_resultsStaging!, 0);
        }

        evaluations += (long)timestampCount * FourKeyCount;
        uint maxCount = 0;
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            uint laneCount = readback[lane * strideUints];
            if (laneCount > maxCount)
            {
                maxCount = laneCount;
            }
        }

        _counterMayBeDirty = maxCount > 0;
        if (maxCount == 0)
        {
            return new IReadOnlyList<uint>[]
            {
                Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>()
            };
        }

        if (maxCount <= (uint)capacity)
        {
            var hits = new IReadOnlyList<uint>[FourKeyCount];
            for (int lane = 0; lane < FourKeyCount; lane++)
            {
                int baseIdx = lane * strideUints;
                int laneCount = (int)readback[baseIdx];
                if (laneCount == 0)
                {
                    hits[lane] = Array.Empty<uint>();
                }
                else
                {
                    uint[] laneHits = new uint[laneCount];
                    Array.Copy(readback, baseIdx + 1, laneHits, 0, laneCount);
                    hits[lane] = laneHits;
                }
            }

            return hits;
        }

        // Output buffer overflowed on at least one lane: split recursively in half (cannot split count==1)
        if (timestampCount == 1)
        {
            throw new InvalidOperationException("Hit buffer overflow on single timestamp.");
        }

        uint half = timestampCount / 2;
        IReadOnlyList<uint>[] left = ScanGroupCore(startTimestamp, half, pattern, capacity, ref evaluations);
        IReadOnlyList<uint>[] right = ScanGroupCore(startTimestamp + half, timestampCount - half, pattern, capacity, ref evaluations);

        var merged = new IReadOnlyList<uint>[FourKeyCount];
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            if (left[lane].Count == 0)
            {
                merged[lane] = right[lane];
            }
            else if (right[lane].Count == 0)
            {
                merged[lane] = left[lane];
            }
            else
            {
                var combined = new List<uint>(left[lane].Count + right[lane].Count);
                combined.AddRange(left[lane]);
                combined.AddRange(right[lane]);
                merged[lane] = combined;
            }
        }

        return merged;
    }

    private void UpdateKeySchedulesIfNeeded(IReadOnlyList<byte[]> publicKeys)
    {
        bool unchanged = _hasCachedKeys;
        if (unchanged)
        {
            for (int lane = 0; lane < FourKeyCount; lane++)
            {
                if (!publicKeys[lane].AsSpan().SequenceEqual(_cachedKeys[lane]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            return;
        }

        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            byte[] key = publicKeys[lane];
            if (key.Length != Ed25519KeyGenerator.KeyByteLength)
            {
                throw new ArgumentException("Each Ed25519 public key must be 32 bytes.", nameof(publicKeys));
            }

            uint[] schedule = Sha1ScheduleHelper.ComputeFixedSchedule(key);
            for (int t = 0; t < Sha1ScheduleHelper.ScheduleRounds; t++)
            {
                // Header is 8 uints (32 bytes); blocks[t] is uint4 at offset 8 + t*4
                _constantStaging[8 + (t * 4) + lane] = schedule[t];
            }

            _cachedKeys[lane] = (byte[])key.Clone();
        }

        _hasCachedKeys = true;
    }

    private void EnsureResultsBuffers(int capacity)
    {
        if (_resultsBuffer is not null && _allocatedCapacity == capacity)
        {
            return;
        }

        _resultsUav?.Dispose();
        _resultsUav = null;
        _resultsBuffer?.Dispose();
        _resultsBuffer = null;
        _resultsStaging?.Dispose();
        _resultsStaging = null;
        _allocatedCapacity = 0;

        uint totalUints = (uint)(FourKeyCount * (capacity + 1));
        uint byteWidth = totalUints * sizeof(uint);

        var gpuDesc = new BufferDescription(
            byteWidth: byteWidth,
            bindFlags: BindFlags.UnorderedAccess,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None,
            miscFlags: ResourceOptionFlags.BufferStructured,
            structureByteStride: sizeof(uint));
        _resultsBuffer = _device.CreateBuffer(gpuDesc);

        var uavDesc = new UnorderedAccessViewDescription(
            _resultsBuffer,
            Format.Unknown,
            firstElement: 0u,
            numElements: totalUints);
        _resultsUav = _device.CreateUnorderedAccessView(_resultsBuffer, uavDesc);

        var stagingDesc = new BufferDescription(
            byteWidth: byteWidth,
            bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging,
            cpuAccessFlags: CpuAccessFlags.Read);
        _resultsStaging = _device.CreateBuffer(stagingDesc);

        _allocatedCapacity = capacity;
        _counterMayBeDirty = true;
    }

    private void UploadConstantBuffer()
    {
        MappedSubresource mapped = _context.Map(_constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                _constantStaging.AsSpan().CopyTo(new Span<uint>((void*)mapped.DataPointer, _constantStaging.Length));
            }
        }
        finally
        {
            _context.Unmap(_constantBuffer, 0);
        }
    }

    private ID3D11ComputeShader CreateComputeShaderWithSelfHealingCache(string? cacheDirectory)
    {
        (byte[] bytecode, bool fromDiskCache, string? cachePath) = GetOrCompileShaderBytecode(cacheDirectory, bypassDiskCache: false);
        try
        {
            return _device.CreateComputeShader(bytecode);
        }
        catch when (fromDiskCache)
        {
            // Corrupted on-disk DXBC cache: purge static & disk cache and recompile once from HLSL
            lock (ShaderCompileLock)
            {
                _cachedShaderBytecode = null;
                if (cachePath is not null)
                {
                    try { File.Delete(cachePath); } catch { }
                }
            }

            (byte[] freshBytecode, _, _) = GetOrCompileShaderBytecode(cacheDirectory, bypassDiskCache: true);
            return _device.CreateComputeShader(freshBytecode);
        }
    }

    private static (byte[] Bytecode, bool FromDiskCache, string? CachePath) GetOrCompileShaderBytecode(
        string? cacheDirectory,
        bool bypassDiskCache)
    {
        if (!bypassDiskCache && _cachedShaderBytecode is not null)
        {
            return (_cachedShaderBytecode, false, null);
        }

        lock (ShaderCompileLock)
        {
            if (!bypassDiskCache && _cachedShaderBytecode is not null)
            {
                return (_cachedShaderBytecode, false, null);
            }

            string hlsl = DirectComputeShaderSource.BuildFourKeyHlsl();
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hlsl)))[..16].ToLowerInvariant();
            string? cachePath = string.IsNullOrWhiteSpace(cacheDirectory)
                ? null
                : Path.Combine(cacheDirectory, $"{DirectComputeShaderSource.KernelVersion}-{hash}.dxbc");

            if (!bypassDiskCache && cachePath is not null && File.Exists(cachePath))
            {
                try
                {
                    byte[] diskBytes = File.ReadAllBytes(cachePath);
                    if (diskBytes.Length > 0)
                    {
                        _cachedShaderBytecode = diskBytes;
                        return (diskBytes, true, cachePath);
                    }
                }
                catch
                {
                    // Recompile if cache file is unreadable.
                }
            }

            ReadOnlyMemory<byte> compiled = Compiler.Compile(
                hlsl,
                entryPoint: "CSMain",
                sourceName: "sha1_four.hlsl",
                profile: "cs_5_0",
                shaderFlags: ShaderFlags.OptimizationLevel3);

            byte[] bytecode = compiled.ToArray();
            _cachedShaderBytecode = bytecode;

            if (cachePath is not null)
            {
                try
                {
                    Directory.CreateDirectory(cacheDirectory!);
                    string tmpPath = Path.Combine(cacheDirectory!, $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.WriteAllBytes(tmpPath, bytecode);
                        File.Move(tmpPath, cachePath, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                    }
                }
                catch
                {
                    // Disk cache is best-effort.
                }
            }

            return (bytecode, false, cachePath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resultsUav?.Dispose();
        _resultsUav = null;
        _resultsBuffer?.Dispose();
        _resultsBuffer = null;
        _resultsStaging?.Dispose();
        _resultsStaging = null;
        _allocatedCapacity = 0;
        _constantBuffer.Dispose();
        _shader.Dispose();
        _context.Dispose();
        _device.Dispose();
        _adapter.Dispose();
    }
}
