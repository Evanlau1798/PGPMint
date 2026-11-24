using System.Runtime.InteropServices;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed unsafe class OpenClDevice : IGpuSearchDevice
{
    public const int FourKeyCount = 4;
    internal const int DefaultWorkgroupSize = 128;
    internal const int ScheduleParamUints = FourKeyCount * Sha1ScheduleHelper.ScheduleRounds + 4;
    internal const int ScheduleParamBytes = ScheduleParamUints * sizeof(uint);

    private readonly nint _deviceId;
    private readonly bool _supportsFillBuffer;
    private readonly bool _useByValueSchedule;
    private readonly nint _context;
    private readonly nint _queue;
    private readonly nint _program;
    private readonly nint _kernel;
    private readonly nint _blockBuffer;

    private readonly nuint _groupSize;
    private readonly ulong _candidatesPerGroup;

    private nint _resultsBuffer;
    private uint[]? _resultsHost;
    private GCHandle _resultsHostPin;
    private int _allocatedCapacity;
    private bool _counterMayBeDirty = true;

    private bool _hasLastScalarArgs;
    private uint _lastFirstArg;
    private uint _lastCountArg;
    private uint _lastLowMaskArg;
    private uint _lastLowTargetArg;
    private uint _lastHighMaskArg;
    private uint _lastHighTargetArg;
    private uint _lastCapacityArg;
    private uint _lastModeArg;

    private readonly byte[][] _cachedKeys = new byte[FourKeyCount][];
    private readonly uint[] _scheduleParamHost = new uint[ScheduleParamUints];
    private bool _hasCachedKeys;
    private bool _disposed;

    public GpuDeviceInfo Info { get; }
    public int KeysPerBatch => FourKeyCount;
    public long MaxCandidatesPerBatch { get; }
    public string KernelVersion => OpenClKernelSource.KernelVersion;

    internal bool LoadedProgramBinary { get; }

    internal static GpuKernelConfiguration ConfigurationFor(nuint maxWorkgroupSize, bool useByValueSchedule = true) =>
        new(FourKeyCount, (int)Math.Clamp(maxWorkgroupSize, 1, DefaultWorkgroupSize), 1,
            useByValueSchedule ? "cmem0-v4" : "constptr-v4");

    internal OpenClDevice(
        nint deviceId,
        GpuDeviceInfo info,
        bool supportsFillBuffer,
        string programCacheDirectory,
        nuint maxParameterSize = ScheduleParamBytes)
    {
        _deviceId = deviceId;
        _supportsFillBuffer = supportsFillBuffer;
        _useByValueSchedule = maxParameterSize >= ScheduleParamBytes;

        Array.Copy(Sha1ScheduleHelper.PhaseConstants, 0, _scheduleParamHost, FourKeyCount * Sha1ScheduleHelper.ScheduleRounds, 4);

        // Keep a coarse device ceiling above the adaptive batch limit.
        MaxCandidatesPerBatch = info.DedicatedVideoMemoryBytes >= 6UL * 1024UL * 1024UL * 1024UL
            ? (1L << 30)
            : info.DedicatedVideoMemoryBytes >= 4UL * 1024UL * 1024UL * 1024UL
                ? (1L << 29)
                : (1L << 27);

        int err = 0;
        nint dev = deviceId;
        _context = OpenClNative.clCreateContext(null, 1, &dev, 0, 0, &err);
        if (err != OpenClNative.ClSuccess || _context == 0)
        {
            throw new InvalidOperationException($"clCreateContext failed ({err}) for '{info.Name}'.");
        }

        try
        {
            _queue = OpenClNative.clCreateCommandQueue(_context, deviceId, 0, &err);
            if (err != OpenClNative.ClSuccess || _queue == 0)
            {
                throw new InvalidOperationException($"clCreateCommandQueue failed ({err}).");
            }

            (_program, _kernel, LoadedProgramBinary) = OpenClProgramCache.Create(
                _context, deviceId, info, programCacheDirectory, _useByValueSchedule);

            nuint kernelMaxGroup = DefaultWorkgroupSize;
            OpenClNative.clGetKernelWorkGroupInfo(_kernel, deviceId, OpenClNative.ClKernelWorkGroupSize, (nuint)sizeof(nuint), &kernelMaxGroup, null);
            GpuKernelConfiguration configuration = ConfigurationFor(kernelMaxGroup, _useByValueSchedule);
            _groupSize = (nuint)configuration.WorkgroupSize;
            Info = info with { KernelConfiguration = configuration };
            _candidatesPerGroup = (ulong)_groupSize * (info.IsDiscrete ? 32UL : 8UL);

            if (!_useByValueSchedule)
            {
                _blockBuffer = OpenClNative.clCreateBuffer(_context, OpenClNative.ClMemReadOnly, (nuint)ScheduleParamBytes, null, &err);
                if (err != OpenClNative.ClSuccess || _blockBuffer == 0)
                {
                    throw new InvalidOperationException($"clCreateBuffer(block) failed ({err}).");
                }

                nint blockArg = _blockBuffer;
                CheckCl(OpenClNative.clSetKernelArg(_kernel, 0, (nuint)sizeof(nint), &blockArg), "clSetKernelArg(0)");
            }
        }
        catch
        {
            Dispose();
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
        UploadKeySchedulesIfNeeded(publicKeys);
        EnsureResultsBuffer(resultCapacity);
        int distinctKeys = GpuSearchBatch.CountDistinctKeys(publicKeys);
        return GpuSearchBatch.Run(FourKeyCount, distinctKeys, startTimestamp, timestampCount, resultCapacity, resultMode, cancellationToken,
            (first, count) => Dispatch(first, count, plan, resultCapacity, resultMode));
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
        nuint totalBytes = (nuint)(totalUints * sizeof(uint));

        if (_counterMayBeDirty)
        {
            bool filled = false;
            if (_supportsFillBuffer)
            {
                uint zero = 0;
                int status = OpenClNative.clEnqueueFillBuffer(_queue, _resultsBuffer, &zero, sizeof(uint), 0, totalBytes, 0, null, null);
                if (status == OpenClNative.ClDeviceNotAvailable)
                {
                    CheckCl(status, "clEnqueueFillBuffer");
                }
                filled = status == OpenClNative.ClSuccess;
            }

            if (!filled)
            {
                Array.Clear(_resultsHost!, 0, totalUints);
                uint* hostPtr = (uint*)_resultsHostPin.AddrOfPinnedObject();
                CheckCl(OpenClNative.clEnqueueWriteBuffer(_queue, _resultsBuffer, 1, 0, totalBytes, hostPtr, 0, null, null), "clEnqueueWriteBuffer(clear)");
            }
        }

        _counterMayBeDirty = true;

        uint firstArg = startTimestamp;
        uint countArg = timestampCount;
        uint lowMaskArg = plan.LowMask;
        uint lowTargetArg = plan.LowTarget;
        uint highMaskArg = plan.HighMask;
        uint highTargetArg = plan.HighTarget;
        uint capArg = (uint)capacity;
        uint modeArg = (uint)resultMode;

        if (!_hasLastScalarArgs)
        {
            nint resultsArg = _resultsBuffer;
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 1, (nuint)sizeof(nint), &resultsArg), "clSetKernelArg(1)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 2, sizeof(uint), &firstArg), "clSetKernelArg(2)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 3, sizeof(uint), &countArg), "clSetKernelArg(3)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 4, sizeof(uint), &lowMaskArg), "clSetKernelArg(4)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 5, sizeof(uint), &lowTargetArg), "clSetKernelArg(5)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 6, sizeof(uint), &highMaskArg), "clSetKernelArg(6)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 7, sizeof(uint), &highTargetArg), "clSetKernelArg(7)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 8, sizeof(uint), &capArg), "clSetKernelArg(8)");
            CheckCl(OpenClNative.clSetKernelArg(_kernel, 9, sizeof(uint), &modeArg), "clSetKernelArg(9)");
            _hasLastScalarArgs = true;
            _lastFirstArg = firstArg;
            _lastCountArg = countArg;
            _lastLowMaskArg = lowMaskArg;
            _lastLowTargetArg = lowTargetArg;
            _lastHighMaskArg = highMaskArg;
            _lastHighTargetArg = highTargetArg;
            _lastCapacityArg = capArg;
            _lastModeArg = modeArg;
        }
        else
        {
            if (firstArg != _lastFirstArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 2, sizeof(uint), &firstArg), "clSetKernelArg(2)"); _lastFirstArg = firstArg; }
            if (countArg != _lastCountArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 3, sizeof(uint), &countArg), "clSetKernelArg(3)"); _lastCountArg = countArg; }
            if (lowMaskArg != _lastLowMaskArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 4, sizeof(uint), &lowMaskArg), "clSetKernelArg(4)"); _lastLowMaskArg = lowMaskArg; }
            if (lowTargetArg != _lastLowTargetArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 5, sizeof(uint), &lowTargetArg), "clSetKernelArg(5)"); _lastLowTargetArg = lowTargetArg; }
            if (highMaskArg != _lastHighMaskArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 6, sizeof(uint), &highMaskArg), "clSetKernelArg(6)"); _lastHighMaskArg = highMaskArg; }
            if (highTargetArg != _lastHighTargetArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 7, sizeof(uint), &highTargetArg), "clSetKernelArg(7)"); _lastHighTargetArg = highTargetArg; }
            if (capArg != _lastCapacityArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 8, sizeof(uint), &capArg), "clSetKernelArg(8)"); _lastCapacityArg = capArg; }
            if (modeArg != _lastModeArg) { CheckCl(OpenClNative.clSetKernelArg(_kernel, 9, sizeof(uint), &modeArg), "clSetKernelArg(9)"); _lastModeArg = modeArg; }
        }

        ulong groups = Math.Max(1UL, ((ulong)timestampCount + _candidatesPerGroup - 1UL) / _candidatesPerGroup);
        nuint globalSize = (nuint)(groups * _groupSize);
        nuint localSize = _groupSize;

        CheckCl(OpenClNative.clEnqueueNDRangeKernel(_queue, _kernel, 1, null, &globalSize, &localSize, 0, null, null), "clEnqueueNDRangeKernel");

        uint[] readback = _resultsHost!;
        uint* readPtr = (uint*)_resultsHostPin.AddrOfPinnedObject();
        CheckCl(OpenClNative.clEnqueueReadBuffer(_queue, _resultsBuffer, 1, 0, totalBytes, readPtr, 0, null, null), "clEnqueueReadBuffer");

        _counterMayBeDirty = GpuSearchBatch.HasHits(readback, strideUints, FourKeyCount);
        return readback;
    }

    private void UploadKeySchedulesIfNeeded(IReadOnlyList<byte[]> publicKeys)
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
            if (publicKeys[lane].Length != Ed25519KeyGenerator.KeyByteLength)
            {
                throw new ArgumentException("Each Ed25519 public key must be 32 bytes.", nameof(publicKeys));
            }
        }

        _hasCachedKeys = false;
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            uint[] sched = Sha1ScheduleHelper.GetOrComputeFixedSchedule(publicKeys[lane]);
            for (int t = 0; t < Sha1ScheduleHelper.ScheduleRounds; t++)
            {
                _scheduleParamHost[t * FourKeyCount + lane] = sched[t];
            }
        }

        fixed (uint* ptr = _scheduleParamHost)
        {
            if (_useByValueSchedule)
            {
                CheckCl(OpenClNative.clSetKernelArg(_kernel, 0, (nuint)ScheduleParamBytes, ptr), "clSetKernelArg(0)");
            }
            else
            {
                CheckCl(OpenClNative.clEnqueueWriteBuffer(_queue, _blockBuffer, 1, 0, (nuint)ScheduleParamBytes, ptr, 0, null, null), "clEnqueueWriteBuffer(blocks)");
            }
        }

        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            _cachedKeys[lane] = (byte[])publicKeys[lane].Clone();
        }

        _hasCachedKeys = true;
    }

    private void EnsureResultsBuffer(int capacity)
    {
        if (_resultsBuffer != 0 && _allocatedCapacity == capacity)
        {
            return;
        }

        ReleaseResultsResources();

        int totalUints = FourKeyCount * (capacity + 1);
        nuint bytes = (nuint)(totalUints * sizeof(uint));
        int err = 0;
        _resultsBuffer = OpenClNative.clCreateBuffer(_context, OpenClNative.ClMemReadWrite | OpenClNative.ClMemAllocHostPtr, bytes, null, &err);
        if (err != OpenClNative.ClSuccess || _resultsBuffer == 0)
        {
            _resultsBuffer = OpenClNative.clCreateBuffer(_context, OpenClNative.ClMemReadWrite, bytes, null, &err);
        }

        CheckCl(err, "clCreateBuffer(results)");
        if (_resultsBuffer == 0)
        {
            throw new InvalidOperationException($"clCreateBuffer(results) failed ({err}).");
        }

        _resultsHost = new uint[totalUints];
        _resultsHostPin = GCHandle.Alloc(_resultsHost, GCHandleType.Pinned);
        _allocatedCapacity = capacity;
        _counterMayBeDirty = true;
        _hasLastScalarArgs = false;
    }

    private void ReleaseResultsResources()
    {
        if (_resultsBuffer != 0)
        {
            OpenClNative.clReleaseMemObject(_resultsBuffer);
            _resultsBuffer = 0;
        }

        if (_resultsHostPin.IsAllocated)
        {
            _resultsHostPin.Free();
        }

        _resultsHost = null;
        _allocatedCapacity = 0;
        _hasLastScalarArgs = false;
    }

    private static void CheckCl(int err, string operation)
    {
        if (err == OpenClNative.ClDeviceNotAvailable)
        {
            throw new GpuDeviceLostException($"OpenCL {operation} failed because the GPU is unavailable ({err}).");
        }

        if (err != OpenClNative.ClSuccess)
        {
            throw new InvalidOperationException($"OpenCL {operation} failed with error code {err}.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseResultsResources();
        if (_blockBuffer != 0) { OpenClNative.clReleaseMemObject(_blockBuffer); }
        if (_kernel != 0) { OpenClNative.clReleaseKernel(_kernel); }
        if (_program != 0) { OpenClNative.clReleaseProgram(_program); }
        if (_queue != 0) { OpenClNative.clReleaseCommandQueue(_queue); }
        if (_context != 0) { OpenClNative.clReleaseContext(_context); }
    }
}
