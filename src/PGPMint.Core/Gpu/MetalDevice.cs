using System.Runtime.InteropServices;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

internal interface IMetalSession : IDisposable
{
    int WorkgroupSize { get; }
    void Dispatch(uint[] schedule, MetalDispatchParameters parameters, uint[] results);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct MetalDispatchParameters(
    uint First, uint Count, uint LowMask, uint LowTarget,
    uint HighMask, uint HighTarget, uint Capacity, uint ResultMode);

public sealed class MetalDevice : IGpuSearchDevice
{
    public const int FourKeyCount = 4;
    private readonly IMetalSession _session;
    private readonly object _gate = new();
    private readonly uint[] _schedule = new uint[FourKeyCount * Sha1ScheduleHelper.ScheduleRounds + 4];
    private readonly byte[][] _cachedKeys = new byte[FourKeyCount][];
    private uint[] _results = [];
    private bool _disposed;
    public GpuDeviceInfo Info { get; }
    public int KeysPerBatch => FourKeyCount;
    public long MaxCandidatesPerBatch => 1L << 27;
    public string KernelVersion => MetalKernelSource.KernelVersion;

    internal MetalDevice(GpuDeviceInfo info, IMetalSession session)
    {
        ArgumentNullException.ThrowIfNull(info);
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Info = info with
        {
            BackendName = "Metal",
            KernelVersion = KernelVersion,
            KernelConfiguration = new(FourKeyCount, session.WorkgroupSize, 1, "constant-msl-v1")
        };
        Sha1ScheduleHelper.PhaseConstants.CopyTo(_schedule, _schedule.Length - 4);
    }

    public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp,
        uint timestampCount, KeyIdPattern pattern, int capacity = 1024) =>
        ScanBatch(publicKeys, startTimestamp, timestampCount, pattern, capacity).Hits;

    public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp,
        uint timestampCount, KeyIdPattern pattern, int capacity = 1024,
        SearchResultMode resultMode = SearchResultMode.EnumerateAll, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(publicKeys);
            ArgumentNullException.ThrowIfNull(pattern);
            if (publicKeys.Count != FourKeyCount)
                throw new ArgumentException($"Expected exactly {FourKeyCount} public keys.", nameof(publicKeys));
            if (timestampCount == 0 || (ulong)startTimestamp + timestampCount > (1UL << 32)
                || (long)timestampCount * FourKeyCount > MaxCandidatesPerBatch)
                throw new ArgumentOutOfRangeException(nameof(timestampCount));
            if (capacity < 1 || capacity > Array.MaxLength / (sizeof(uint) * FourKeyCount) - 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            int resultCapacity = resultMode switch
            {
                SearchResultMode.FindOne => 1,
                SearchResultMode.EnumerateAll => capacity,
                _ => throw new ArgumentOutOfRangeException(nameof(resultMode))
            };
            if (cancellationToken.IsCancellationRequested)
                return SearchBatchResult.CancelledBeforeDispatch(FourKeyCount);
            PatternPlan plan = PatternPlan.From(pattern);
            UploadKeySchedules(publicKeys);
            int resultLength = checked(FourKeyCount * (resultCapacity + 1));
            if (_results.Length != resultLength)
                _results = new uint[resultLength];
            return GpuSearchBatch.Run(FourKeyCount, GpuSearchBatch.CountDistinctKeys(publicKeys),
                startTimestamp, timestampCount, resultCapacity, resultMode, cancellationToken, (first, count) =>
                {
                    var parameters = new MetalDispatchParameters(first, count, plan.LowMask, plan.LowTarget,
                        plan.HighMask, plan.HighTarget, (uint)resultCapacity, (uint)resultMode);
                    _session.Dispatch(_schedule, parameters, _results);
                    return _results;
                });
        }
    }

    private void UploadKeySchedules(IReadOnlyList<byte[]> publicKeys)
    {
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            if (publicKeys[lane] is not { Length: Ed25519KeyGenerator.KeyByteLength })
                throw new ArgumentException("Each Ed25519 public key must be 32 bytes.", nameof(publicKeys));
        }
        for (int lane = 0; lane < FourKeyCount; lane++)
        {
            if (_cachedKeys[lane] is not null && publicKeys[lane].AsSpan().SequenceEqual(_cachedKeys[lane]))
                continue;
            byte[] key = (byte[])publicKeys[lane].Clone();
            uint[] words = Sha1ScheduleHelper.ComputeFixedSchedule(key);
            for (int round = 0; round < words.Length; round++)
                _schedule[round * FourKeyCount + lane] = words[round];
            _cachedKeys[lane] = key;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
            _results = [];
        }
    }
}
