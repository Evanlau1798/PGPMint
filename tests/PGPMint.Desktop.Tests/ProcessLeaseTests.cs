using System.Diagnostics;
using Xunit;

namespace PGPMint.Desktop.Tests;

public sealed class ProcessLeaseTests
{
    private static ProcessStartInfo Command(string command, string unixCommand)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh");
        string[] args = OperatingSystem.IsWindows() ? ["-NoProfile", "-NonInteractive", "-Command", command] : ["-c", unixCommand];
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    [Fact]
    public async Task ReadyProcessRemainsOwnedUntilDisposedAndIsReaped()
    {
        var info = Command("[Console]::WriteLine('READY'); [Console]::ReadLine() | Out-Null", "printf 'READY\\n'; read -r line");
        var lease = await ProcessLease.StartAsync(info, "READY", TimeSpan.FromSeconds(5));
        int id = lease.ProcessId;
        Assert.True(lease.IsRunning);
        await lease.DisposeAsync();
        Assert.False(lease.IsRunning);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(id));
    }

    [Fact]
    public async Task FailedReadinessIsNotReportedAsSuccessfulInhibition()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessLease.StartAsync(Command("exit 9", "exit 9"), "READY", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MissingReadinessTimesOutAndKillsTheOwnedProcess()
    {
        var info = Command("[Console]::ReadLine() | Out-Null", "read -r line");
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessLease.StartAsync(info, "READY", TimeSpan.FromMilliseconds(300)));
    }
}
