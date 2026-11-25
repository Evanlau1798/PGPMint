using System.Runtime.InteropServices;
using PGPMint.Core.Search;

namespace PGPMint.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTrayIcon = WmApp + 100;
    private const uint WmCommand = 0x0111;
    private const uint WmNull = 0x0000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmContextMenu = 0x007B;
    private const uint NinKeySelect = 0x0401;
    private const uint NinBalloonUserClick = 0x0405;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifShowTip = 0x00000080;
    private const uint NiifInfo = 0x00000001;

    private const uint MfString = 0x00000000;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;
    private const uint MfChecked = 0x00000008;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;

    private const uint CmdOpen = 1001;
    private const uint CmdToggle = 1002;
    private const uint CmdToggleMiniHud = 1003;
    private const uint CmdQuit = 1004;
    private const uint CmdTierEco = 1010;
    private const uint CmdTierBalanced = 1011;
    private const uint CmdTierEfficiency = 1012;
    private const uint CmdTierMax = 1013;

    private readonly string _className = "PGPMintTrayHost_" + Guid.NewGuid().ToString("N")[..8];
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreatedMsg;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _iconAdded;
    private bool _isMenuOpen;
    private bool _disposed;

    private string _tooltip = "PGPMint";
    private string _summaryLine1 = string.Empty;
    private string _summaryLine2 = string.Empty;
    private string _openLabel = "Open";
    private string _toggleLabel = "Start";
    private bool _canToggleSearch = true;
    private string _tierEcoLabel = "Eco";
    private string _tierBalancedLabel = "Balanced";
    private string _tierEfficiencyLabel = "Efficiency";
    private string _tierMaxLabel = "Max";
    private PowerTier _activeTier = PowerTier.Max;
    private bool _canSelectTier;
    private string _miniHudLabel = "Floating HUD";
    private bool _miniHudChecked = true;
    private string _quitLabel = "Quit";

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleSearchRequested;
    public event EventHandler<PowerTier>? PowerTierRequested;
    public event EventHandler? ToggleMiniHudRequested;
    public event EventHandler? QuitRequested;

    public IntPtr AppIconHandle => _hIcon;

    public TrayIconService()
    {
        _wndProc = WindowProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        _hIcon = CreateProgrammaticIcon();
        CreateMessageWindow();
        AddTrayIcon();
    }

    public void UpdateMenuState(
        string openLabel,
        string toggleLabel,
        bool canToggleSearch,
        string tierEcoLabel,
        string tierBalancedLabel,
        string tierEfficiencyLabel,
        string tierMaxLabel,
        PowerTier activeTier,
        bool canSelectTier,
        string miniHudLabel,
        bool miniHudChecked,
        string quitLabel,
        string summaryLine1,
        string summaryLine2)
    {
        _openLabel = openLabel;
        _toggleLabel = toggleLabel;
        _canToggleSearch = canToggleSearch;
        _tierEcoLabel = tierEcoLabel;
        _tierBalancedLabel = tierBalancedLabel;
        _tierEfficiencyLabel = tierEfficiencyLabel;
        _tierMaxLabel = tierMaxLabel;
        _activeTier = activeTier;
        _canSelectTier = canSelectTier;
        _miniHudLabel = miniHudLabel;
        _miniHudChecked = miniHudChecked;
        _quitLabel = quitLabel;
        _summaryLine1 = summaryLine1;
        _summaryLine2 = summaryLine2;
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        string safe = string.IsNullOrEmpty(tooltip) ? "PGPMint" : tooltip;
        if (safe.Length > 127)
        {
            safe = safe[..127];
        }

        if (string.Equals(_tooltip, safe, StringComparison.Ordinal))
        {
            return;
        }

        _tooltip = safe;
        var data = CreateBaseNotifyData();
        data.uFlags = NifTip | NifShowTip;
        data.szTip = _tooltip;
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void ShowNotification(string title, string message)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateBaseNotifyData();
        data.uFlags = NifInfo | NifTip | NifShowTip;
        data.dwInfoFlags = NiifInfo;
        data.szTip = _tooltip;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        Shell_NotifyIconW(NimModify, ref data);
    }

    public void ShowContextMenuAtCursor()
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowContextMenu();
    }

    private void CreateMessageWindow()
    {
        IntPtr hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className
        };

        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(
            0,
            _className,
            "PGPMintTrayHost",
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);
    }

    private void AddTrayIcon()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = CreateBaseNotifyData();
        data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.uCallbackMessage = WmTrayIcon;
        data.hIcon = _hIcon;
        data.szTip = _tooltip;

        _iconAdded = Shell_NotifyIconW(NimAdd, ref data);
        if (_iconAdded)
        {
            data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4
            Shell_NotifyIconW(NimSetVersion, ref data);
        }
    }

    private NOTIFYICONDATAW CreateBaseNotifyData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            AddTrayIcon();
            return IntPtr.Zero;
        }

        if (msg == WmTrayIcon)
        {
            uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg is WmLButtonUp or WmLButtonDblClk or NinKeySelect or NinBalloonUserClick)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }

            if (mouseMsg == WmContextMenu)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }
        else if (msg == WmCommand)
        {
            uint cmdId = (uint)(wParam.ToInt64() & 0xFFFF);
            switch (cmdId)
            {
                case CmdOpen:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdToggle:
                    ToggleSearchRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdTierEco:
                    PowerTierRequested?.Invoke(this, PowerTier.Eco);
                    break;
                case CmdTierBalanced:
                    PowerTierRequested?.Invoke(this, PowerTier.Balanced);
                    break;
                case CmdTierEfficiency:
                    PowerTierRequested?.Invoke(this, PowerTier.Efficiency);
                    break;
                case CmdTierMax:
                    PowerTierRequested?.Invoke(this, PowerTier.Max);
                    break;
                case CmdToggleMiniHud:
                    ToggleMiniHudRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CmdQuit:
                    QuitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_disposed || _hwnd == IntPtr.Zero || _isMenuOpen)
        {
            return;
        }

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        _isMenuOpen = true;
        try
        {
            bool hasHeader = false;
            if (!string.IsNullOrWhiteSpace(_summaryLine1))
            {
                AppendMenuW(menu, MfString | MfGrayed | MfDisabled, 0, _summaryLine1);
                hasHeader = true;
            }
            if (!string.IsNullOrWhiteSpace(_summaryLine2))
            {
                AppendMenuW(menu, MfString | MfGrayed | MfDisabled, 0, _summaryLine2);
                hasHeader = true;
            }
            if (hasHeader)
            {
                AppendMenuW(menu, MfSeparator, 0, null);
            }

            AppendMenuW(menu, MfString, CmdOpen, _openLabel);
            AppendMenuW(menu, MfString | (_canToggleSearch ? 0u : (MfGrayed | MfDisabled)), CmdToggle, _toggleLabel);
            AppendMenuW(menu, MfSeparator, 0, null);
            uint tierDisabled = _canSelectTier ? 0u : (MfGrayed | MfDisabled);
            AppendMenuW(menu, MfString | tierDisabled | (_activeTier == PowerTier.Eco ? MfChecked : 0u), CmdTierEco, _tierEcoLabel);
            AppendMenuW(menu, MfString | tierDisabled | (_activeTier == PowerTier.Balanced ? MfChecked : 0u), CmdTierBalanced, _tierBalancedLabel);
            AppendMenuW(menu, MfString | tierDisabled | (_activeTier == PowerTier.Efficiency ? MfChecked : 0u), CmdTierEfficiency, _tierEfficiencyLabel);
            AppendMenuW(menu, MfString | tierDisabled | (_activeTier == PowerTier.Max ? MfChecked : 0u), CmdTierMax, _tierMaxLabel);
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString | (_miniHudChecked ? MfChecked : 0u), CmdToggleMiniHud, _miniHudLabel);
            AppendMenuW(menu, MfSeparator, 0, null);
            AppendMenuW(menu, MfString, CmdQuit, _quitLabel);
            SetMenuDefaultItem(menu, CmdOpen, 0);

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);
            TrackPopupMenuEx(menu, TpmRightButton | TpmBottomAlign, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            if (_hwnd != IntPtr.Zero)
            {
                PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            _isMenuOpen = false;
            DestroyMenu(menu);
        }
    }

    private static IntPtr CreateProgrammaticIcon()
    {
        const int size = 32;
        uint[] pixels = new uint[size * size];

        const double center = (size - 1) / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = Math.Abs(x - center);
                double dy = Math.Abs(y - center);
                double d = Math.Max(Math.Max(dx, dy), (dx + dy) * 0.72);
                double inner = dx + dy;

                uint argb;
                if (d > 14.5)
                {
                    argb = 0x00000000u;
                }
                else if (d > 12.6)
                {
                    argb = 0xFF0E9F6Eu;
                }
                else if (inner < 5.2 && !(dx < 2.2 && dy < 2.2))
                {
                    argb = 0xFF34D399u;
                }
                else if (dx < 2.2 && dy < 2.2)
                {
                    argb = 0xFF0B1F1Au;
                }
                else
                {
                    argb = 0xFF102A23u;
                }

                pixels[y * size + x] = argb;
            }
        }

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };

        IntPtr hColor = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
        if (hColor == IntPtr.Zero)
        {
            return LoadIconW(IntPtr.Zero, (IntPtr)32512);
        }

        IntPtr hMask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        try
        {
            unsafe
            {
                pixels.AsSpan().CopyTo(new Span<uint>((void*)bitsPtr, pixels.Length));
            }

            var iconInfo = new ICONINFO
            {
                fIcon = true,
                hbmColor = hColor,
                hbmMask = hMask
            };

            IntPtr hIcon = CreateIconIndirect(ref iconInfo);
            return hIcon != IntPtr.Zero ? hIcon : LoadIconW(IntPtr.Zero, (IntPtr)32512);
        }
        finally
        {
            DeleteObject(hColor);
            if (hMask != IntPtr.Zero)
            {
                DeleteObject(hMask);
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
        if (_iconAdded && _hwnd != IntPtr.Zero)
        {
            var data = CreateBaseNotifyData();
            Shell_NotifyIconW(NimDelete, ref data);
            _iconAdded = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClassW(_className, GetModuleHandleW(null));

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
