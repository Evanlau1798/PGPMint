using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PGPMint.Core.Search;

/// <summary>
/// Manages Windows process/thread power-throttling opt-out (disabling EcoQoS and background timer
/// coalescing) and dedicated worker threads so background/tray GPU compute maintains full dispatch
/// cadence on Windows 11 hybrid P/E-core CPUs, restoring OS defaults when compute stops or pauses.
/// </summary>
public static class WindowsComputeQoS
{
    private const int ProcessPowerThrottling = 4;
    private const int ThreadPowerThrottling = 3;
    private const uint PowerThrottlingCurrentVersion = 1u;
    private const uint ExecutionSpeedMask = 0x1u;
    private const uint IgnoreTimerResolutionMask = 0x4u;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private sealed class ProcessScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseProcessScope();
            }
        }
    }

    private static readonly object Gate = new();
    private static int _activeScopeCount;
    private static ProcessPriorityClass _savedPriorityClass = ProcessPriorityClass.Normal;
    private static bool _elevatedPriority;
    private static bool _lastProcessPowerThrottlingApplied;

    public static bool IsProcessHighPerformanceActive
    {
        get
        {
            lock (Gate)
            {
                return _activeScopeCount > 0;
            }
        }
    }

    public static bool LastProcessPowerThrottlingApplied
    {
        get
        {
            lock (Gate)
            {
                return _lastProcessPowerThrottlingApplied;
            }
        }
    }

    public static IDisposable AcquireProcessScope()
    {
        lock (Gate)
        {
            _activeScopeCount++;
            if (_activeScopeCount == 1)
            {
                EnableProcessHighPerformanceLocked();
            }
        }

        return new ProcessScope();
    }

    public static Task<T> RunDedicatedWorker<T>(Func<T> work, string threadName, bool aboveNormalPriority)
    {
        ArgumentNullException.ThrowIfNull(work);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                TryDisableCurrentThreadPowerThrottling();
                T result = work();
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = string.IsNullOrWhiteSpace(threadName) ? "PGPMint-Worker" : threadName,
            Priority = aboveNormalPriority ? ThreadPriority.AboveNormal : ThreadPriority.Normal
        };

        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    public static Task RunDedicatedWorker(Action work, string threadName, bool aboveNormalPriority)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunDedicatedWorker(() =>
        {
            work();
            return true;
        }, threadName, aboveNormalPriority);
    }

    private static void ReleaseProcessScope()
    {
        lock (Gate)
        {
            if (_activeScopeCount <= 0)
            {
                return;
            }

            _activeScopeCount--;
            if (_activeScopeCount == 0)
            {
                RestoreProcessDefaultsLocked();
            }
        }
    }

    private static void EnableProcessHighPerformanceLocked()
    {
        _elevatedPriority = false;
        try
        {
            using Process current = Process.GetCurrentProcess();
            ProcessPriorityClass prior = current.PriorityClass;
            if (prior is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
            {
                _savedPriorityClass = prior;
                current.PriorityClass = ProcessPriorityClass.AboveNormal;
                _elevatedPriority = true;
            }
        }
        catch
        {
            // Keep running if OS policy restricts priority elevation.
        }

        _lastProcessPowerThrottlingApplied = ApplyProcessPowerThrottling(disableThrottling: true);
    }

    private static void RestoreProcessDefaultsLocked()
    {
        ApplyProcessPowerThrottling(disableThrottling: false);

        if (_elevatedPriority)
        {
            try
            {
                using Process current = Process.GetCurrentProcess();
                current.PriorityClass = _savedPriorityClass;
            }
            catch
            {
                // Ignore restoration failure during shutdown.
            }
            finally
            {
                _elevatedPriority = false;
            }
        }
    }

    private static bool ApplyProcessPowerThrottling(bool disableThrottling)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            IntPtr hProcess = GetCurrentProcess();
            uint size = (uint)Marshal.SizeOf<PowerThrottlingState>();

            if (disableThrottling)
            {
                // Disable both EcoQoS execution-speed throttling and Windows 11 background timer coalescing.
                var fullState = new PowerThrottlingState
                {
                    Version = PowerThrottlingCurrentVersion,
                    ControlMask = ExecutionSpeedMask | IgnoreTimerResolutionMask,
                    StateMask = 0u
                };

                if (SetProcessInformation(hProcess, ProcessPowerThrottling, ref fullState, size))
                {
                    return true;
                }

                // Older Windows 10 builds only recognize EXECUTION_SPEED.
                var fallbackState = new PowerThrottlingState
                {
                    Version = PowerThrottlingCurrentVersion,
                    ControlMask = ExecutionSpeedMask,
                    StateMask = 0u
                };

                return SetProcessInformation(hProcess, ProcessPowerThrottling, ref fallbackState, size);
            }

            // Return control back to Windows automatic power management.
            var resetState = new PowerThrottlingState
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = 0u,
                StateMask = 0u
            };

            return SetProcessInformation(hProcess, ProcessPowerThrottling, ref resetState, size);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDisableCurrentThreadPowerThrottling()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var state = new PowerThrottlingState
            {
                Version = PowerThrottlingCurrentVersion,
                ControlMask = ExecutionSpeedMask,
                StateMask = 0u
            };

            SetThreadInformation(
                GetCurrentThread(),
                ThreadPowerThrottling,
                ref state,
                (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch
        {
            // Ignore on older OS versions without ThreadPowerThrottling support.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadInformation(
        IntPtr hThread,
        int threadInformationClass,
        ref PowerThrottlingState threadInformation,
        uint threadInformationSize);
}
