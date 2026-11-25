using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PGPMint.Desktop;

internal sealed class SleepInhibitor : IAsyncDisposable
{
    private ProcessLease? _process;
    private uint _assertion;
    private bool _hasAssertion;
    internal bool IsHeld => _hasAssertion || _process?.IsRunning == true;

    internal async Task<bool> AcquireAsync()
    {
        if (IsHeld) return true;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                nint type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", 0x08000100);
                nint reason = CFStringCreateWithCString(0, "PGPMint GPU search", 0x08000100);
                try
                {
                    _hasAssertion = type != 0 && reason != 0 && IOPMAssertionCreateWithName(type, 255, reason, out _assertion) == 0;
                    return _hasAssertion;
                }
                finally { if (type != 0) CFRelease(type); if (reason != 0) CFRelease(reason); }
            }
            if (OperatingSystem.IsLinux())
            {
                if (_process is not null) await _process.DisposeAsync();
                var info = new ProcessStartInfo("/usr/bin/systemd-inhibit");
                foreach (string arg in new[] { "--what=sleep", "--mode=block", "--who=PGPMint", "--why=GPU key search",
                    "/bin/sh", "-c", "printf 'PGPMINT_READY\\n'; exec /bin/cat >/dev/null" }) info.ArgumentList.Add(arg);
                _process = await ProcessLease.StartAsync(info, "PGPMINT_READY", TimeSpan.FromSeconds(3));
                return IsHeld;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null) { await _process.DisposeAsync(); _process = null; }
        if (_hasAssertion)
        {
            if (IOPMAssertionRelease(_assertion) != 0) throw new InvalidOperationException("Could not release sleep assertion.");
            _hasAssertion = false;
        }
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint obj);
    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);
    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint id);
}
