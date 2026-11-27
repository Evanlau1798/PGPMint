using System.Diagnostics;
using PGPMint.Core.Search;

namespace PGPMint.Cli;

internal static class HitHookRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static string ValidateExecutablePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || rawPath.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException("--on-hit-exec requires a valid executable path.");
        }

        string trimmed = rawPath.Trim();
        if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar) || Path.IsPathRooted(trimmed))
        {
            string full = Path.GetFullPath(trimmed);
            if (!File.Exists(full))
            {
                throw new ArgumentException($"--on-hit-exec target file not found: {full}");
            }

            return full;
        }

        return trimmed;
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, MatchedKeyRecord match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var psi = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = true
        };
        // Public non-secret metadata only; private seeds never leave PGPMint's vault.
        psi.Environment["PGPMINT_KEY_ID"] = match.KeyId;
        psi.Environment["PGPMINT_FINGERPRINT"] = match.Fingerprint;
        psi.Environment["PGPMINT_MASK"] = match.MatchedPattern;
        psi.Environment["PGPMINT_DEVICE"] = match.DeviceName;
        psi.Environment["PGPMINT_FOUND_AT_UTC"] = match.FoundAtUtc.ToString("O");
        return psi;
    }

    internal static async Task<int> InvokeAsync(
        string executablePath,
        MatchedKeyRecord match,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        TextWriter? errorOutput = null)
    {
        TextWriter err = errorOutput ?? Console.Error;
        ProcessStartInfo psi = CreateStartInfo(executablePath, match);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                err.WriteLine($"Hook failed to start: {executablePath}");
                return -1;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                string stderrText = await proc.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                if (proc.ExitCode != 0)
                {
                    string detail = string.IsNullOrWhiteSpace(stderrText) ? string.Empty : $" ({stderrText.Trim()})";
                    err.WriteLine($"Hook exited with code {proc.ExitCode}{detail}.");
                }

                return proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                err.WriteLine($"Hook timed out or cancelled: {executablePath}");
                return -1;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            err.WriteLine($"Hook execution failed: {ex.Message}");
            return -1;
        }
    }
}
