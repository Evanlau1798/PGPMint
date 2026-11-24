using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using PGPMint.Core.Crypto;

namespace PGPMint.Core.Search;

internal readonly record struct PreparedKeyGroup(Ed25519KeyPair[] Keys, byte[][] PublicKeys);

/// <summary>
/// Asynchronously pre-generates Ed25519 key groups and warms cached SHA-1 schedules
/// off the GPU dispatch thread, deterministically zeroing buffered seeds on disposal.
/// </summary>
internal sealed class KeySchedulePool : IDisposable
{
    private readonly int _keysPerGroup;
    private readonly int _capacity;
    private readonly Action<Ed25519KeyPair[]>? _onGroupGenerated;
    private readonly object _lock = new();
    private readonly Queue<PreparedKeyGroup> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _producerTask;
    private bool _disposed;

    internal KeySchedulePool(int keysPerGroup, int capacity = 2, Action<Ed25519KeyPair[]>? onGroupGenerated = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keysPerGroup);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _keysPerGroup = keysPerGroup;
        _capacity = capacity;
        _onGroupGenerated = onGroupGenerated;
        _queue = new Queue<PreparedKeyGroup>(capacity);
        _producerTask = WindowsComputeQoS.RunDedicatedWorker(
            ProduceLoop,
            "PGPMint-KeySchedulePool",
            aboveNormalPriority: false);
    }

    internal PreparedKeyGroup TakeNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_queue.Count > 0)
            {
                PreparedKeyGroup buffered = _queue.Dequeue();
                Monitor.PulseAll(_lock);
                return buffered;
            }
        }

        PreparedKeyGroup direct = CreatePreparedGroup();
        if (cancellationToken.IsCancellationRequested)
        {
            ZeroKeys(direct.Keys);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return direct;
    }

    private void ProduceLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                lock (_lock)
                {
                    while (_queue.Count >= _capacity && !_disposed && !_cts.IsCancellationRequested)
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_disposed || _cts.IsCancellationRequested)
                    {
                        return;
                    }
                }

                PreparedKeyGroup group = CreatePreparedGroup();
                lock (_lock)
                {
                    if (_disposed || _cts.IsCancellationRequested)
                    {
                        ZeroKeys(group.Keys);
                        return;
                    }

                    _queue.Enqueue(group);
                    Monitor.PulseAll(_lock);
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            // Stop producer gracefully; caller still generates synchronously if queue empties.
            ExceptionDispatchInfo.Capture(ex);
        }
    }

    private PreparedKeyGroup CreatePreparedGroup()
    {
        var keys = new Ed25519KeyPair[_keysPerGroup];
        var publicKeys = new byte[_keysPerGroup][];
        try
        {
            for (int i = 0; i < _keysPerGroup; i++)
            {
                Ed25519KeyPair pair = Ed25519KeyGenerator.Generate();
                keys[i] = pair;
                publicKeys[i] = pair.PublicKey;
                _ = Sha1ScheduleHelper.GetOrComputeFixedSchedule(pair.PublicKey);
            }

            _onGroupGenerated?.Invoke(keys);
            return new PreparedKeyGroup(keys, publicKeys);
        }
        catch
        {
            ZeroKeys(keys);
            throw;
        }
    }

    internal static void ZeroKeys(Ed25519KeyPair[] keys)
    {
        foreach (Ed25519KeyPair pair in keys)
        {
            if (pair.PrivateSeed is not null)
            {
                CryptographicOperations.ZeroMemory(pair.PrivateSeed);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            Monitor.PulseAll(_lock);
        }

        try
        {
            _producerTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Producer shutdown failures are ignored during cleanup.
        }

        lock (_lock)
        {
            while (_queue.Count > 0)
            {
                ZeroKeys(_queue.Dequeue().Keys);
            }
        }

        _cts.Dispose();
    }
}
