using System.Runtime.InteropServices;

namespace PGPMint.App.Services;

public sealed partial class PowerKeepAliveService : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        EsSystemRequired = 0x00000001u,
        EsContinuous = 0x80000000u
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private bool _active;

    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        SetThreadExecutionState(active
            ? ExecutionState.EsContinuous | ExecutionState.EsSystemRequired
            : ExecutionState.EsContinuous);
    }

    public void Dispose()
    {
        SetActive(false);
    }
}
