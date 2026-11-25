using System.Diagnostics;
using PGPMint.Core.Crypto;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
    private bool HandleVerifiedHits(
        GpuDeviceInfo deviceInfo,
        Ed25519KeyPair[] keys,
        IReadOnlyList<uint>[] laneHits,
        KeyIdPattern pattern,
        bool allowAutoPause,
        CancellationToken failureToken)
    {
        for (int lane = 0; lane < keys.Length; lane++)
        {
            IReadOnlyList<uint> timestamps = laneHits[lane];
            if (timestamps.Count == 0)
            {
                continue;
            }

            uint ts = timestamps[0];
            string fingerprint = OpenPgpV4Packet.ComputeFingerprint(keys[lane].PublicKey, ts);
            ulong keyId = OpenPgpV4Packet.ComputeKeyIdUInt64(keys[lane].PublicKey, ts);
            if (!pattern.Matches(keyId))
            {
                throw new InvalidOperationException(
                    $"GPU hit verification failed on '{deviceInfo.Name}': keyId {keyId:X16} did not match {pattern.CanonicalMask}.");
            }

            lock (_lifecycleLock)
            {
                _sessionWork = _sessionWork with { VerifiedHits = _sessionWork.VerifiedHits + 1 };
            }

            MatchedKeyRecord record;
            bool stopAfterSave;
            lock (_submissionGate)
            {
                if (_submissionFailed || Volatile.Read(ref _autoPauseTriggered) != 0 || failureToken.IsCancellationRequested)
                {
                    return false;
                }

                int storedCount;
                bool wasAdded;
                try
                {
                    record = _vaultStore.SaveMatch(keys[lane].PrivateSeed, keys[lane].PublicKey, ts, fingerprint,
                        pattern.CanonicalMask, deviceInfo.Name, out storedCount, out wasAdded);
                }
                catch (Exception ex)
                {
                    _submissionFailed = true;
                    if (ex is VaultFullException)
                    {
                        _vaultIsFull = true;
                        _errorMessage = ex.Message;
                        throw;
                    }

                    throw new SearchSessionException(SearchFailure.SaveFailed, ex.Message, ex);
                }

                Interlocked.Exchange(ref _totalMatchesCount, storedCount);
                if (!wasAdded)
                {
                    return true;
                }

                lock (_lifecycleLock)
                {
                    _sessionWork = _sessionWork with { SavedKeys = _sessionWork.SavedKeys + 1 };
                }

                _vaultIsFull = storedCount >= DpapiVaultStore.MaxStoredMatches;
                stopAfterSave = _vaultIsFull || (allowAutoPause && _autoPauseOnHit);
                if (stopAfterSave)
                {
                    Interlocked.Exchange(ref _autoPauseTriggered, 1);
                    Interlocked.Increment(ref _rateEpoch);
                }
            }

            if (stopAfterSave)
            {
                _activeOpCts?.Cancel();
            }

            NotifyMatchFound(record);
            return true;
        }

        return false;
    }

    private void NotifyMatchFound(MatchedKeyRecord record)
    {
        if (MatchFound is not { } handlers)
        {
            return;
        }

        foreach (EventHandler<MatchedKeyRecord> listener in handlers.GetInvocationList())
        {
            try
            {
                listener(this, record);
            }
            catch (Exception ex)
            {
                Trace.TraceError("A PGPMint match notification failed: {0}", ex.GetType().Name);
            }
        }
    }

}
