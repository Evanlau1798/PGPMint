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
    internal static readonly GpuKernelConfiguration DefaultConfiguration =
        new(FourKeyCount, DirectComputeShaderSource.ThreadGroupSize, 1, "cb1-v4");
    private const uint MaxDispatchGroups = 65535u;
    private const int SearchParametersUints = 12; // 48 bytes (16-byte aligned cbuffer b0)
    private const int ParamsConstantBufferBytes = SearchParametersUints * sizeof(uint);
    private const int ScheduleConstantBufferUints = (Sha1ScheduleHelper.ScheduleRounds + 1) * FourKeyCount; // 1,248 bytes (b1)
    private const int ScheduleConstantBufferBytes = ScheduleConstantBufferUints * sizeof(uint);

    private static readonly object ShaderCompileLock = new();
    private static byte[]? _cachedShaderBytecode;

    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _paramsBuffer;
    private readonly ID3D11Buffer _scheduleBuffer;
    private readonly uint[] _paramsStaging = new uint[SearchParametersUints];
    private readonly uint[] _scheduleStaging = new uint[ScheduleConstantBufferUints];

    private ID3D11Buffer? _resultsBuffer;
    private ID3D11UnorderedAccessView? _resultsUav;
    private ID3D11Buffer? _resultsStaging;
    private uint[]? _resultsHost;
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
        Info = info with { KernelConfiguration = DefaultConfiguration };

        Array.Copy(Sha1ScheduleHelper.PhaseConstants, 0, _scheduleStaging, Sha1ScheduleHelper.ScheduleRounds * FourKeyCount, 4);

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

            _paramsBuffer = _device.CreateBuffer(new BufferDescription(
                byteWidth: ParamsConstantBufferBytes,
                bindFlags: BindFlags.ConstantBuffer,
                usage: ResourceUsage.Dynamic,
                cpuAccessFlags: CpuAccessFlags.Write));

            _scheduleBuffer = _device.CreateBuffer(new BufferDescription(
                byteWidth: ScheduleConstantBufferBytes,
                bindFlags: BindFlags.ConstantBuffer,
                usage: ResourceUsage.Dynamic,
                cpuAccessFlags: CpuAccessFlags.Write));
        }
        catch
        {
            _scheduleBuffer?.Dispose();
            _paramsBuffer?.Dispose();
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
        int capacity = 1024,
        SearchResultMode resultMode = SearchResultMode.EnumerateAll,
        CancellationToken cancellationToken = default)
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

        int resultCapacity = resultMode switch
        {
            SearchResultMode.FindOne => 1,
            SearchResultMode.EnumerateAll => capacity,
            _ => throw new ArgumentOutOfRangeException(nameof(resultMode))
        };

        if (cancellationToken.IsCancellationRequested)
        {
            return SearchBatchResult.CancelledBeforeDispatch(FourKeyCount);
        }

        PatternPlan plan = PatternPlan.From(pattern);
        try
        {
            UpdateKeySchedulesIfNeeded(publicKeys);
            EnsureResultsBuffers(resultCapacity);
            int distinctKeys = GpuSearchBatch.CountDistinctKeys(publicKeys);
            return GpuSearchBatch.Run(FourKeyCount, distinctKeys, startTimestamp, timestampCount, resultCapacity, resultMode, cancellationToken,
                (first, count) => Dispatch(first, count, plan, resultCapacity, resultMode));
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (_device.DeviceRemovedReason.Failure)
        {
            throw new GpuDeviceLostException($"DirectCompute GPU '{Info.Name}' was lost during search.", ex);
        }
    }

    private uint[] Dispatch(
        uint startTimestamp,
        uint timestampCount,
        PatternPlan plan,
        int capacity,
        SearchResultMode resultMode)
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

        _paramsStaging[0] = startTimestamp;
        _paramsStaging[1] = timestampCount;
        _paramsStaging[2] = plan.LowMask;
        _paramsStaging[3] = plan.LowTarget;
        _paramsStaging[4] = plan.HighMask;
        _paramsStaging[5] = plan.HighTarget;
        _paramsStaging[6] = (uint)capacity;
        _paramsStaging[7] = totalThreads;
        _paramsStaging[8] = (uint)resultMode;
        _paramsStaging[9] = plan.LowTargetExactNoK3;

        UploadConstantBuffer(_paramsBuffer, _paramsStaging);

        _context.CSSetShader(_shader);
        _context.CSSetConstantBuffer(0, _paramsBuffer);
        _context.CSSetConstantBuffer(1, _scheduleBuffer);
        _context.CSSetUnorderedAccessView(0, _resultsUav!);
        _context.Dispatch(dispatchGroups, 1, 1);

        _context.CopyResource(_resultsStaging!, _resultsBuffer!);

        uint[] readback = _resultsHost!;
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

        _counterMayBeDirty = GpuSearchBatch.HasHits(readback, strideUints, FourKeyCount);
        return readback;
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
            if (publicKeys[lane] is not { Length: Ed25519KeyGenerator.KeyByteLength })
            {
                throw new ArgumentException("Each Ed25519 public key must be 32 bytes.", nameof(publicKeys));
            }
        }

        _hasCachedKeys = false;
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            uint[] schedule = Sha1ScheduleHelper.GetOrComputeFixedSchedule(publicKeys[lane]);
            for (int t = 0; t < Sha1ScheduleHelper.ScheduleRounds; t++)
            {
                _scheduleStaging[(t * FourKeyCount) + lane] = schedule[t];
            }
        }

        UploadConstantBuffer(_scheduleBuffer, _scheduleStaging);

        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            _cachedKeys[lane] = (byte[])publicKeys[lane].Clone();
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
        _resultsHost = null;
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

        _resultsHost = new uint[totalUints];
        _allocatedCapacity = capacity;
        _counterMayBeDirty = true;
    }

    private void UploadConstantBuffer(ID3D11Buffer buffer, uint[] staging)
    {
        MappedSubresource mapped = _context.Map(buffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                staging.AsSpan().CopyTo(new Span<uint>((void*)mapped.DataPointer, staging.Length));
            }
        }
        finally
        {
            _context.Unmap(buffer, 0);
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
        _resultsHost = null;
        _allocatedCapacity = 0;
        _scheduleBuffer.Dispose();
        _paramsBuffer.Dispose();
        _shader.Dispose();
        _context.Dispose();
        _device.Dispose();
        _adapter.Dispose();
    }
}
