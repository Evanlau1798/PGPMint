using PGPMint.Core.Storage;

namespace PGPMint.Core.Search;

public sealed partial class MultiGpuCoordinator
{
    public bool DeleteVaultMatch(string recordId)
    {
        lock (_submissionGate)
        {
            if (!_vaultStore.DeleteMatch(recordId, out int remainingCount))
            {
                return false;
            }

            RecordVaultRemoval(remainingCount);
        }

        PublishTelemetry();
        return true;
    }

    public int ClearVaultMatches()
    {
        int removed;
        lock (_submissionGate)
        {
            removed = _vaultStore.ClearAllMatches();
            RecordVaultRemoval(0);
        }

        PublishTelemetry();
        return removed;
    }

    private void RecordVaultRemoval(int remainingCount)
    {
        Interlocked.Exchange(ref _totalMatchesCount, remainingCount);
        lock (_lifecycleLock)
        {
            if (_vaultIsFull && remainingCount < DpapiVaultStore.MaxStoredMatches)
            {
                _vaultIsFull = false;
                _errorMessage = null;
            }
        }
    }

    private bool CheckVaultCapacity()
    {
        lock (_submissionGate)
        {
            int storedCount = _vaultStore.LoadMatches().Count;
            Interlocked.Exchange(ref _totalMatchesCount, storedCount);
            if (storedCount < DpapiVaultStore.MaxStoredMatches)
            {
                return true;
            }

            _vaultIsFull = true;
            _errorMessage = new VaultFullException(DpapiVaultStore.MaxStoredMatches).Message;
            _state = CoordinatorState.Faulted;
            return false;
        }
    }
}
