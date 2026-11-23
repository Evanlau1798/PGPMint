using System.Text;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

public sealed unsafe class OpenClDevice : IGpuSearchDevice
{
    public const int FourKeyCount = 4;

    private readonly nint _deviceId;
    private readonly bool _supportsFillBuffer;
    private readonly nint _context;
    private readonly nint _queue;
    private readonly nint _program;
    private readonly nint _kernel;
    private readonly nint _blockBuffer;

    private readonly nuint _groupSize;
    private readonly ulong _candidatesPerGroup;

    private nint _resultsBuffer;
    private uint[]? _resultsHost;
    private int _allocatedCapacity;
    private bool _counterMayBeDirty = true;

    private readonly byte[][] _cachedKeys = new byte[FourKeyCount][];
    private readonly uint[] _scheduleHost = new uint[FourKeyCount * Sha1ScheduleHelper.ScheduleRounds];
    private bool _hasCachedKeys;
    private bool _disposed;

    public GpuDeviceInfo Info { get; }
    public int KeysPerBatch => FourKeyCount;
    public long MaxCandidatesPerBatch { get; }
    public string KernelVersion => OpenClKernelSource.KernelVersion;

    internal OpenClDevice(nint deviceId, GpuDeviceInfo info, bool supportsFillBuffer = true)
    {
        _deviceId = deviceId;
        _supportsFillBuffer = supportsFillBuffer;
        Info = info;

        // High-VRAM discrete GPUs use 2^30 candidates per full-speed batch (proven optimal on RTX 3080 ~48ms/batch)
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

            byte[] srcBytes = Encoding.ASCII.GetBytes(OpenClKernelSource.BuildFourKeyOpenCl());
            nuint srcLen = (nuint)srcBytes.Length;
            fixed (byte* srcPtr = srcBytes)
            {
                byte* p = srcPtr;
                _program = OpenClNative.clCreateProgramWithSource(_context, 1, &p, &srcLen, &err);
            }
            if (err != OpenClNative.ClSuccess || _program == 0)
            {
                throw new InvalidOperationException($"clCreateProgramWithSource failed ({err}).");
            }

            err = OpenClNative.clBuildProgram(_program, 1, &dev, null, 0, 0);
            if (err != OpenClNative.ClSuccess)
            {
                string log = ReadBuildLog(_program, deviceId);
                throw new InvalidOperationException($"OpenCL kernel build failed ({err}): {log}");
            }

            byte[] nameBytes = Encoding.ASCII.GetBytes("search\0");
            fixed (byte* namePtr = nameBytes)
            {
                _kernel = OpenClNative.clCreateKernel(_program, namePtr, &err);
            }
            if (err != OpenClNative.ClSuccess || _kernel == 0)
            {
                throw new InvalidOperationException($"clCreateKernel failed ({err}).");
            }

            nuint kernelMaxGroup = 128;
            OpenClNative.clGetKernelWorkGroupInfo(_kernel, deviceId, OpenClNative.ClKernelWorkGroupSize, (nuint)sizeof(nuint), &kernelMaxGroup, null);
            _groupSize = kernelMaxGroup < 128 ? Math.Max((nuint)1, kernelMaxGroup) : (nuint)128;
            _candidatesPerGroup = (ulong)_groupSize * (info.IsDiscrete ? 32UL : 8UL);

            nuint blockBytes = (nuint)(FourKeyCount * Sha1ScheduleHelper.ScheduleRounds * sizeof(uint));
            _blockBuffer = OpenClNative.clCreateBuffer(_context, OpenClNative.ClMemReadOnly, blockBytes, null, &err);
            if (err != OpenClNative.ClSuccess || _blockBuffer == 0)
            {
                throw new InvalidOperationException($"clCreateBuffer(block) failed ({err}).");
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

        UploadKeySchedulesIfNeeded(publicKeys);
        EnsureResultsBuffer(resultCapacity);
        return GpuSearchBatch.Run(FourKeyCount, startTimestamp, timestampCount, resultCapacity, resultMode, cancellationToken,
            (first, count) => Dispatch(first, count, pattern, resultCapacity, resultMode));
    }

    private uint[] Dispatch(
        uint startTimestamp,
        uint timestampCount,
        KeyIdPattern pattern,
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
                filled = OpenClNative.clEnqueueFillBuffer(_queue, _resultsBuffer, &zero, sizeof(uint), 0, totalBytes, 0, null, null) == OpenClNative.ClSuccess;
            }

            if (!filled)
            {
                Array.Clear(_resultsHost!, 0, totalUints);
                fixed (uint* hostPtr = _resultsHost)
                {
                    CheckCl(OpenClNative.clEnqueueWriteBuffer(_queue, _resultsBuffer, 1, 0, totalBytes, hostPtr, 0, null, null), "clEnqueueWriteBuffer(clear)");
                }
            }
        }

        _counterMayBeDirty = true;

        nint blockArg = _blockBuffer;
        nint resultsArg = _resultsBuffer;
        uint firstArg = startTimestamp;
        uint countArg = timestampCount;
        ulong maskArg = pattern.Mask;
        ulong targetArg = pattern.Target;
        uint capArg = (uint)capacity;
        uint modeArg = (uint)resultMode;

        CheckCl(OpenClNative.clSetKernelArg(_kernel, 0, (nuint)sizeof(nint), &blockArg), "clSetKernelArg(0)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 1, (nuint)sizeof(nint), &resultsArg), "clSetKernelArg(1)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 2, sizeof(uint), &firstArg), "clSetKernelArg(2)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 3, sizeof(uint), &countArg), "clSetKernelArg(3)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 4, sizeof(ulong), &maskArg), "clSetKernelArg(4)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 5, sizeof(ulong), &targetArg), "clSetKernelArg(5)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 6, sizeof(uint), &capArg), "clSetKernelArg(6)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 7, sizeof(uint), &modeArg), "clSetKernelArg(7)");

        ulong groups = Math.Max(1UL, ((ulong)timestampCount + _candidatesPerGroup - 1UL) / _candidatesPerGroup);
        nuint globalSize = (nuint)(groups * _groupSize);
        nuint localSize = _groupSize;

        CheckCl(OpenClNative.clEnqueueNDRangeKernel(_queue, _kernel, 1, null, &globalSize, &localSize, 0, null, null), "clEnqueueNDRangeKernel");

        uint[] readback = _resultsHost!;
        fixed (uint* hostPtr = readback)
        {
            CheckCl(OpenClNative.clEnqueueReadBuffer(_queue, _resultsBuffer, 1, 0, totalBytes, hostPtr, 0, null, null), "clEnqueueReadBuffer");
        }

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

        // Validate all 4 keys before mutating schedule or cache state
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
            uint[] sched = Sha1ScheduleHelper.ComputeFixedSchedule(publicKeys[lane]);
            Array.Copy(sched, 0, _scheduleHost, lane * Sha1ScheduleHelper.ScheduleRounds, Sha1ScheduleHelper.ScheduleRounds);
        }

        nuint bytes = (nuint)(_scheduleHost.Length * sizeof(uint));
        fixed (uint* ptr = _scheduleHost)
        {
            CheckCl(OpenClNative.clEnqueueWriteBuffer(_queue, _blockBuffer, 1, 0, bytes, ptr, 0, null, null), "clEnqueueWriteBuffer(blocks)");
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

        if (_resultsBuffer != 0)
        {
            OpenClNative.clReleaseMemObject(_resultsBuffer);
            _resultsBuffer = 0;
        }
        _resultsHost = null;
        _allocatedCapacity = 0;

        int totalUints = FourKeyCount * (capacity + 1);
        nuint bytes = (nuint)(totalUints * sizeof(uint));
        int err = 0;
        _resultsBuffer = OpenClNative.clCreateBuffer(_context, OpenClNative.ClMemReadWrite, bytes, null, &err);
        if (err != OpenClNative.ClSuccess || _resultsBuffer == 0)
        {
            throw new InvalidOperationException($"clCreateBuffer(results) failed ({err}).");
        }

        _resultsHost = new uint[totalUints];
        _allocatedCapacity = capacity;
        _counterMayBeDirty = true;
    }

    private static void CheckCl(int err, string operation)
    {
        if (err != OpenClNative.ClSuccess)
        {
            throw new InvalidOperationException($"OpenCL {operation} failed with error code {err}.");
        }
    }

    private static string ReadBuildLog(nint program, nint device)
    {
        nuint logSize = 0;
        if (OpenClNative.clGetProgramBuildInfo(program, device, OpenClNative.ClProgramBuildLog, 0, null, &logSize) != OpenClNative.ClSuccess || logSize == 0)
        {
            return "(no build log)";
        }

        byte[] buf = new byte[(int)Math.Min(logSize, 8192)];
        fixed (byte* ptr = buf)
        {
            OpenClNative.clGetProgramBuildInfo(program, device, OpenClNative.ClProgramBuildLog, (nuint)buf.Length, ptr, null);
        }

        int len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len >= 0 ? len : buf.Length).Trim();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_resultsBuffer != 0) { OpenClNative.clReleaseMemObject(_resultsBuffer); _resultsBuffer = 0; }
        if (_blockBuffer != 0) { OpenClNative.clReleaseMemObject(_blockBuffer); }
        if (_kernel != 0) { OpenClNative.clReleaseKernel(_kernel); }
        if (_program != 0) { OpenClNative.clReleaseProgram(_program); }
        if (_queue != 0) { OpenClNative.clReleaseCommandQueue(_queue); }
        if (_context != 0) { OpenClNative.clReleaseContext(_context); }
    }
}
