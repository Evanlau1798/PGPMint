using System.Text;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

internal sealed record OpenClAdapterDescriptor(
    nint DeviceId,
    string Name,
    uint VendorId,
    string DriverVersion,
    ulong GlobalMemoryBytes,
    long? Luid,
    bool SupportsFillBuffer);

internal static unsafe class OpenClEnumerator
{
    public static IReadOnlyList<OpenClAdapterDescriptor> TryEnumerateGpuAdapters()
    {
        var results = new List<OpenClAdapterDescriptor>();
        if (!OpenClNative.IsAvailable())
        {
            return results;
        }

        try
        {
            uint platformCount = 0;
            if (OpenClNative.clGetPlatformIDs(0, null, &platformCount) != OpenClNative.ClSuccess || platformCount == 0)
            {
                return results;
            }

            nint[] platforms = new nint[platformCount];
            fixed (nint* platPtr = platforms)
            {
                if (OpenClNative.clGetPlatformIDs(platformCount, platPtr, null) != OpenClNative.ClSuccess)
                {
                    return results;
                }
            }

            foreach (nint platform in platforms)
            {
                uint devCount = 0;
                int err = OpenClNative.clGetDeviceIDs(platform, OpenClNative.ClDeviceTypeGpu, 0, null, &devCount);
                if (err != OpenClNative.ClSuccess || devCount == 0)
                {
                    continue;
                }

                nint[] devs = new nint[devCount];
                fixed (nint* devPtr = devs)
                {
                    if (OpenClNative.clGetDeviceIDs(platform, OpenClNative.ClDeviceTypeGpu, devCount, devPtr, null) != OpenClNative.ClSuccess)
                    {
                        continue;
                    }
                }

                foreach (nint dev in devs)
                {
                    string name = GetDeviceString(dev, OpenClNative.ClDeviceName).Trim();
                    string driver = GetDeviceString(dev, OpenClNative.ClDriverVersion).Trim();
                    string version = GetDeviceString(dev, OpenClNative.ClDeviceVersion).Trim();
                    string extensions = GetDeviceString(dev, OpenClNative.ClDeviceExtensions);

                    uint vendorId = 0;
                    OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceVendorId, (nuint)sizeof(uint), &vendorId, null);

                    ulong globalMem = 0;
                    OpenClNative.clGetDeviceInfo(dev, OpenClNative.ClDeviceGlobalMemSize, (nuint)sizeof(ulong), &globalMem, null);

                    long? luid = TryReadLuid(dev, extensions);
                    bool supportsFill = OpenClNative.SupportsFillBufferExport() && IsOpenCl12OrNewer(version);
                    results.Add(new OpenClAdapterDescriptor(dev, name, vendorId, driver, globalMem, luid, supportsFill));
                }
            }
        }
        catch
        {
            // OpenCL is optional accelerator; fail softly and allow DirectCompute fallback
        }

        return results;
    }

    private static string GetDeviceString(nint device, uint paramName)
    {
        nuint size = 0;
        if (OpenClNative.clGetDeviceInfo(device, paramName, 0, null, &size) != OpenClNative.ClSuccess || size == 0)
        {
            return string.Empty;
        }

        byte[] buf = new byte[(int)size];
        fixed (byte* ptr = buf)
        {
            if (OpenClNative.clGetDeviceInfo(device, paramName, size, ptr, null) != OpenClNative.ClSuccess)
            {
                return string.Empty;
            }
        }

        int len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len >= 0 ? len : buf.Length);
    }

    private static long? TryReadLuid(nint device, string extensions)
    {
        if (!extensions.Contains("cl_khr_device_uuid", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        uint valid = 0;
        if (OpenClNative.clGetDeviceInfo(device, OpenClNative.ClDeviceLuidValidKhr, (nuint)sizeof(uint), &valid, null) != OpenClNative.ClSuccess || valid == 0)
        {
            return null;
        }

        byte[] luidBytes = new byte[8];
        fixed (byte* ptr = luidBytes)
        {
            if (OpenClNative.clGetDeviceInfo(device, OpenClNative.ClDeviceLuidKhr, 8, ptr, null) != OpenClNative.ClSuccess)
            {
                return null;
            }
        }

        uint low = BitConverter.ToUInt32(luidBytes, 0);
        int high = BitConverter.ToInt32(luidBytes, 4);
        return ((long)high << 32) | low;
    }

    private static bool IsOpenCl12OrNewer(string deviceVersion)
    {
        // Standard OpenCL format: "OpenCL <major>.<minor> ..."
        string[] parts = deviceVersion.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        string[] ver = parts[1].Split('.');
        if (ver.Length < 2 || !int.TryParse(ver[0], out int major) || !int.TryParse(ver[1], out int minor))
        {
            return false;
        }

        return major > 1 || (major == 1 && minor >= 2);
    }
}

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

        UploadKeySchedulesIfNeeded(publicKeys);
        EnsureResultsBuffer(capacity);

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

        CheckCl(OpenClNative.clSetKernelArg(_kernel, 0, (nuint)sizeof(nint), &blockArg), "clSetKernelArg(0)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 1, (nuint)sizeof(nint), &resultsArg), "clSetKernelArg(1)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 2, sizeof(uint), &firstArg), "clSetKernelArg(2)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 3, sizeof(uint), &countArg), "clSetKernelArg(3)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 4, sizeof(ulong), &maskArg), "clSetKernelArg(4)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 5, sizeof(ulong), &targetArg), "clSetKernelArg(5)");
        CheckCl(OpenClNative.clSetKernelArg(_kernel, 6, sizeof(uint), &capArg), "clSetKernelArg(6)");

        ulong groups = Math.Max(1UL, ((ulong)timestampCount + _candidatesPerGroup - 1UL) / _candidatesPerGroup);
        nuint globalSize = (nuint)(groups * _groupSize);
        nuint localSize = _groupSize;

        CheckCl(OpenClNative.clEnqueueNDRangeKernel(_queue, _kernel, 1, null, &globalSize, &localSize, 0, null, null), "clEnqueueNDRangeKernel");

        uint[] readback = _resultsHost!;
        fixed (uint* hostPtr = readback)
        {
            CheckCl(OpenClNative.clEnqueueReadBuffer(_queue, _resultsBuffer, 1, 0, totalBytes, hostPtr, 0, null, null), "clEnqueueReadBuffer");
        }

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

        if (timestampCount == 1)
        {
            throw new InvalidOperationException("Hit buffer overflow on single timestamp.");
        }

        uint half = timestampCount / 2;
        IReadOnlyList<uint>[] left = ScanGroup(publicKeys, startTimestamp, half, pattern, capacity);
        IReadOnlyList<uint>[] right = ScanGroup(publicKeys, startTimestamp + half, timestampCount - half, pattern, capacity);

        var merged = new IReadOnlyList<uint>[FourKeyCount];
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            if (left[lane].Count == 0) merged[lane] = right[lane];
            else if (right[lane].Count == 0) merged[lane] = left[lane];
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
