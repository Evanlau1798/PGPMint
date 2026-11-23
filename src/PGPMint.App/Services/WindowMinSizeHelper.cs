using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace PGPMint.App.Services;

/// <summary>
/// Enforces native Win32 minimum track size via WM_GETMINMAXINFO using comctl32 SetWindowSubclass,
/// and provides monitor work-area centering and clamped auto-sizing.
/// </summary>
public sealed class WindowMinSizeHelper : IDisposable
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
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

    public static double GetDpiScale(IntPtr hwnd)
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

    public static void CenterAndResizeOnScreen(AppWindow appWindow, IntPtr hwnd, int logicalWidth, int logicalHeight)
    {
        double scale = GetDpiScale(hwnd);
        int widthPx = (int)Math.Round(logicalWidth * scale);
        int heightPx = (int)Math.Round(logicalHeight * scale);

        if (TryGetMonitorWorkArea(hwnd, MonitorDefaultToPrimary, out RECT wa))
        {
            int workW = Math.Max(320, wa.Right - wa.Left);
            int workH = Math.Max(320, wa.Bottom - wa.Top);
            widthPx = Math.Min(workW - 16, widthPx);
            heightPx = Math.Min(workH - 16, heightPx);
            int x = wa.Left + Math.Max(0, (workW - widthPx) / 2);
            int y = wa.Top + Math.Max(0, (workH - heightPx) / 2);
            appWindow.MoveAndResize(new RectInt32(x, y, widthPx, heightPx));
        }
        else
        {
            appWindow.Resize(new SizeInt32(widthPx, heightPx));
        }
    }

    public static void ResizeHeightClampedToWorkArea(
        AppWindow appWindow,
        IntPtr hwnd,
        int targetLogicalHeight,
        int minLogicalHeight,
        bool centerOnMonitor = false)
    {
        double scale = GetDpiScale(hwnd);
        int minHeightPx = (int)Math.Round(minLogicalHeight * scale);
        int targetHeightPx = Math.Max(minHeightPx, (int)Math.Round(targetLogicalHeight * scale));
        int curWidthPx = appWindow.Size.Width;
        int curX = appWindow.Position.X;
        int curY = appWindow.Position.Y;

        if (TryGetMonitorWorkArea(hwnd, MonitorDefaultToNearest, out RECT wa))
        {
            int workW = Math.Max(320, wa.Right - wa.Left);
            int workH = Math.Max(320, wa.Bottom - wa.Top);
            int maxAllowedH = Math.Max(minHeightPx, workH - 20);
            targetHeightPx = Math.Min(targetHeightPx, maxAllowedH);

            if (centerOnMonitor)
            {
                curX = wa.Left + Math.Max(0, (workW - curWidthPx) / 2);
                curY = wa.Top + Math.Max(0, (workH - targetHeightPx) / 2);
            }
            else if (curY + targetHeightPx > wa.Bottom - 8)
            {
                curY = Math.Max(wa.Top + 8, wa.Bottom - targetHeightPx - 8);
            }
        }

        if (targetHeightPx != appWindow.Size.Height || curX != appWindow.Position.X || curY != appWindow.Position.Y)
        {
            appWindow.MoveAndResize(new RectInt32(curX, curY, curWidthPx, targetHeightPx));
        }
    }

    private static bool TryGetMonitorWorkArea(IntPtr hwnd, uint flags, out RECT workArea)
    {
        workArea = default;
        IntPtr hMon = MonitorFromWindow(hwnd, flags);
        if (hMon == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(hMon, ref info))
        {
            return false;
        }

        workArea = info.rcWork;
        return true;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

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
