using System.Runtime.InteropServices;
using PGPMint.Core.Search;

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
    private IDisposable? _qosScope;

    public void SetActive(bool active)
    {
        if (_active == active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            _qosScope ??= WindowsComputeQoS.AcquireProcessScope();
            SetThreadExecutionState(ExecutionState.EsContinuous | ExecutionState.EsSystemRequired);
        }
        else
        {
            _qosScope?.Dispose();
            _qosScope = null;
            SetThreadExecutionState(ExecutionState.EsContinuous);
        }
    }

    public void Dispose()
    {
        SetActive(false);
    }
}
