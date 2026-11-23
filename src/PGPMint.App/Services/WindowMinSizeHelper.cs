using System.Runtime.InteropServices;

namespace PGPMint.App.Services;

/// <summary>
/// Enforces native Win32 minimum track size via WM_GETMINMAXINFO using comctl32 SetWindowSubclass.
/// Prevents top/left border drag from pushing the window across the screen.
/// </summary>
public sealed class WindowMinSizeHelper : IDisposable
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr SubclassId = new(0x5047504Du);

    private readonly IntPtr _hwnd;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private readonly SubclassProc _procDelegate;
    private bool _installed;
    private bool _disposed;

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        IntPtr dwRefData);

    public WindowMinSizeHelper(IntPtr hwnd, int minWidthDip, int minHeightDip)
    {
        _hwnd = hwnd;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        _procDelegate = OnSubclassProc;

        if (_hwnd != IntPtr.Zero)
        {
            _installed = SetWindowSubclass(_hwnd, _procDelegate, SubclassId, IntPtr.Zero);
        }
    }

    private IntPtr OnSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        IntPtr dwRefData)
    {
        if (uMsg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
        {
            IntPtr res = DefSubclassProc(hWnd, uMsg, wParam, lParam);
            double scale = GetDpiScale(hWnd);
            MINMAXINFO mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, (int)Math.Round(_minWidthDip * scale));
            mmi.ptMinTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, (int)Math.Round(_minHeightDip * scale));
            Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
            return res;
        }

        if (uMsg == WmNcDestroy)
        {
            Uninstall();
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static double GetDpiScale(IntPtr hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi >= 96 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private void Uninstall()
    {
        if (_installed && _hwnd != IntPtr.Zero)
        {
            _installed = false;
            try
            {
                RemoveWindowSubclass(_hwnd, _procDelegate, SubclassId);
            }
            catch
            {
                // Ignore if window is already destroyed
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass,
        IntPtr dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc pfnSubclass,
        UIntPtr uIdSubclass);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint uMsg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
