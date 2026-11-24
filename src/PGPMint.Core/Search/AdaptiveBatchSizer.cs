namespace PGPMint.Core.Search;

internal sealed class AdaptiveBatchSizer
{
    private const long InitialCandidateLimit = 1L << 18;
    private const double TargetWallMilliseconds = 30;
    private readonly int _keysPerBatch;
    private readonly uint _maxTimestamps;
    private readonly uint _initialTimestamps;
    private double _lastDispatchWallMilliseconds;

    internal AdaptiveBatchSizer(long maxCandidates, int keysPerBatch, int suffixHexLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keysPerBatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCandidates, keysPerBatch);
        ArgumentOutOfRangeException.ThrowIfLessThan(suffixHexLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(suffixHexLength, 16);

        _keysPerBatch = keysPerBatch;
        long candidateLimit = maxCandidates;
        if (suffixHexLength <= 4)
        {
            double probability = 1.0 / (1L << (4 * suffixHexLength));
            long likelyHitLimit = (long)Math.Ceiling(Math.Log(0.01) / Math.Log(1.0 - probability));
            candidateLimit = Math.Min(maxCandidates, Math.Max(512L, likelyHitLimit));
        }

        _maxTimestamps = (uint)Math.Clamp(candidateLimit / keysPerBatch, 1, uint.MaxValue);
        _initialTimestamps = Math.Min(_maxTimestamps, (uint)Math.Max(1, InitialCandidateLimit / keysPerBatch));
        NextTimestampLimit = _initialTimestamps;
    }

    internal uint NextTimestampLimit { get; private set; }
    internal long NextCandidateLimit => (long)NextTimestampLimit * _keysPerBatch;
    internal double LastDispatchWallMilliseconds => Volatile.Read(ref _lastDispatchWallMilliseconds);

    internal void Observe(uint completedTimestamps, double dispatchWallMilliseconds)
    {
        if (completedTimestamps == 0 || completedTimestamps > _maxTimestamps ||
            !double.IsFinite(dispatchWallMilliseconds) || dispatchWallMilliseconds <= 0)
        {
            Volatile.Write(ref _lastDispatchWallMilliseconds, 0);
            NextTimestampLimit = _initialTimestamps;
            return;
        }

        Volatile.Write(ref _lastDispatchWallMilliseconds, dispatchWallMilliseconds);
        if (completedTimestamps < NextTimestampLimit && dispatchWallMilliseconds <= TargetWallMilliseconds)
        {
            return;
        }

        double desired = completedTimestamps * TargetWallMilliseconds / dispatchWallMilliseconds;
        uint minimum = Math.Max(1u, completedTimestamps / 2);
        ulong maximum = Math.Min(_maxTimestamps, (ulong)completedTimestamps * 2);
        NextTimestampLimit = (uint)Math.Clamp(desired, minimum, maximum);
    }
}
