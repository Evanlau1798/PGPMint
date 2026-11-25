using System.Diagnostics;

namespace PGPMint.Desktop;

internal sealed class ProcessLease : IAsyncDisposable
{
    private Process? _process;
    private ProcessLease(Process process) => _process = process;
    internal int ProcessId => _process?.Id ?? 0;
    internal bool IsRunning => _process is { HasExited: false };

    internal static async Task<ProcessLease> StartAsync(ProcessStartInfo info, string ready, TimeSpan timeout)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start native helper.");
        }
        catch { process.Dispose(); throw; }
        var lease = new ProcessLease(process);
        process.BeginErrorReadLine();
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(timeout);
            if (line != ready || process.HasExited) throw new InvalidOperationException("Native helper did not acquire the requested service.");
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not { } process) return;
        if (!process.HasExited)
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        process.Dispose();
        _process = null;
    }
}
