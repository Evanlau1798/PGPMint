using System.Diagnostics;
using System.Runtime.InteropServices;
using PGPMint.Core.Search;
using Xunit;

// MultiGpuCoordinator and WindowsComputeQoSTests exercise process-wide Win32 priority and
// ProcessPowerThrottling state, so disable cross-collection parallel test execution to prevent races.

namespace PGPMint.Tests;

public sealed class WindowsComputeQoSTests
{
    private const int ProcessPowerThrottling = 4;
    private const int ThreadPowerThrottling = 3;
    private const uint PowerThrottlingCurrentVersion = 1u;
    private const uint ExecutionSpeedMask = 0x1u;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PowerThrottlingState processInformation,
        uint processInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadInformation(
        IntPtr hThread,
        int threadInformationClass,
        ref PowerThrottlingState threadInformation,
        uint threadInformationSize);

    [Fact]
    public void AcquireProcessScopeDisablesPowerThrottlingElevatesPriorityAndRestoresOnNestedDispose()
    {
        ProcessPriorityClass initialPriority = GetLiveProcessPriorityClass();
        ProcessPriorityClass expectedActivePriority = initialPriority is ProcessPriorityClass.Normal
            or ProcessPriorityClass.BelowNormal
            or ProcessPriorityClass.Idle
                ? ProcessPriorityClass.AboveNormal
                : initialPriority;
        Assert.False(WindowsComputeQoS.IsProcessHighPerformanceActive);

        IDisposable outer = WindowsComputeQoS.AcquireProcessScope();
        try
        {
            Assert.True(WindowsComputeQoS.IsProcessHighPerformanceActive);
            Assert.True(WindowsComputeQoS.LastProcessPowerThrottlingApplied);
            Assert.Equal(expectedActivePriority, GetLiveProcessPriorityClass());

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                PowerThrottlingState activeState = QueryProcessPowerThrottling();
                Assert.True((activeState.ControlMask & ExecutionSpeedMask) != 0,
                    $"Expected EXECUTION_SPEED ControlMask set, got 0x{activeState.ControlMask:X}.");
                Assert.Equal(0u, activeState.StateMask & ExecutionSpeedMask);
            }

            IDisposable inner = WindowsComputeQoS.AcquireProcessScope();
            try
            {
                Assert.True(WindowsComputeQoS.IsProcessHighPerformanceActive);
                Assert.Equal(expectedActivePriority, GetLiveProcessPriorityClass());
            }
            finally
            {
                inner.Dispose();
                // Verify idempotent double dispose does not prematurely release outer scope
                inner.Dispose();
            }

            Assert.True(WindowsComputeQoS.IsProcessHighPerformanceActive);
            Assert.Equal(expectedActivePriority, GetLiveProcessPriorityClass());
        }
        finally
        {
            outer.Dispose();
            outer.Dispose();
        }

        Assert.False(WindowsComputeQoS.IsProcessHighPerformanceActive);
        Assert.Equal(initialPriority, GetLiveProcessPriorityClass());

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            PowerThrottlingState restoredState = QueryProcessPowerThrottling();
            Assert.Equal(0u, restoredState.ControlMask & ExecutionSpeedMask);
        }
    }

    private static ProcessPriorityClass GetLiveProcessPriorityClass()
    {
        using Process current = Process.GetCurrentProcess();
        return current.PriorityClass;
    }

    [Fact]
    public async Task RunDedicatedWorkerUsesNonThreadPoolThreadWithHighQoSAndRequestedPriority()
    {
        var elevated = await WindowsComputeQoS.RunDedicatedWorker(() =>
        {
            bool isPool = Thread.CurrentThread.IsThreadPoolThread;
            ThreadPriority priority = Thread.CurrentThread.Priority;
            string? name = Thread.CurrentThread.Name;
            PowerThrottlingState? qos = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                ? QueryThreadPowerThrottling()
                : null;
            return (isPool, priority, name, qos);
        }, "TestGpuWorker", aboveNormalPriority: true);

        Assert.False(elevated.isPool);
        Assert.Equal(ThreadPriority.AboveNormal, elevated.priority);
        Assert.Equal("TestGpuWorker", elevated.name);
        if (elevated.qos.HasValue)
        {
            Assert.True((elevated.qos.Value.ControlMask & ExecutionSpeedMask) != 0);
            Assert.Equal(0u, elevated.qos.Value.StateMask & ExecutionSpeedMask);
        }

        var normal = await WindowsComputeQoS.RunDedicatedWorker(() =>
        {
            bool isPool = Thread.CurrentThread.IsThreadPoolThread;
            ThreadPriority priority = Thread.CurrentThread.Priority;
            PowerThrottlingState? qos = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                ? QueryThreadPowerThrottling()
                : null;
            return (isPool, priority, qos);
        }, "TestKeyPoolWorker", aboveNormalPriority: false);

        Assert.False(normal.isPool);
        Assert.Equal(ThreadPriority.Normal, normal.priority);
        if (normal.qos.HasValue)
        {
            Assert.True((normal.qos.Value.ControlMask & ExecutionSpeedMask) != 0);
            Assert.Equal(0u, normal.qos.Value.StateMask & ExecutionSpeedMask);
        }
    }

    [Fact]
    public async Task RunDedicatedWorkerPropagatesExceptionsCleanly()
    {
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WindowsComputeQoS.RunDedicatedWorker(
                new Action(() => throw new InvalidOperationException("boom-worker")),
                "TestFaultWorker",
                aboveNormalPriority: true));

        Assert.Equal("boom-worker", ex.Message);
    }

    private static PowerThrottlingState QueryProcessPowerThrottling()
    {
        var state = new PowerThrottlingState { Version = PowerThrottlingCurrentVersion };
        bool ok = GetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottling,
            ref state,
            (uint)Marshal.SizeOf<PowerThrottlingState>());
        Assert.True(ok, $"GetProcessInformation(ProcessPowerThrottling) failed with Win32 error {Marshal.GetLastWin32Error()}.");
        return state;
    }

    private static PowerThrottlingState QueryThreadPowerThrottling()
    {
        var state = new PowerThrottlingState { Version = PowerThrottlingCurrentVersion };
        bool ok = GetThreadInformation(
            GetCurrentThread(),
            ThreadPowerThrottling,
            ref state,
            (uint)Marshal.SizeOf<PowerThrottlingState>());
        Assert.True(ok, $"GetThreadInformation(ThreadPowerThrottling) failed with Win32 error {Marshal.GetLastWin32Error()}.");
        return state;
    }
}
