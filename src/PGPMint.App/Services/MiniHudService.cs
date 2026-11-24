using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PGPMint.Core.Search;
using Vortice;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2D1FactoryType = Vortice.Direct2D1.FactoryType;
using D2D1ResultCode = Vortice.Direct2D1.ResultCode;
using D2D1TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using DWriteFontStyle = Vortice.DirectWrite.FontStyle;

namespace PGPMint.App.Services;

public sealed record MiniHudSnapshot(
    CoordinatorState State,
    string RateText,
    string ProbabilityText,
    string EtaText,
    double ProgressFraction);

/// <summary>
/// Pure CPU Win32 layered floating HUD drawn via Direct2D Software RenderTarget
/// (RenderTargetType.Software) + DirectWrite grayscale antialiasing and UpdateLayeredWindow.
/// Uses zero hardware GPU / WinUI 3 resources while delivering crisp modern vector and text quality.
/// </summary>
public sealed class MiniHudService : IDisposable
{
    private const int LogicalWidth = 268;
    private const int LogicalHeight = 38;
    private const int DockMarginDip = 12;

    private const uint CsDblClks = 0x0008;
    private const uint WsPopup = 0x80000000u;
    private const uint WsExTopMost = 0x00000008u;
    private const uint WsExToolWindow = 0x00000080u;
    private const uint WsExLayered = 0x00080000u;
    private const uint WsExNoActivate = 0x08000000u;

    private const uint WmMouseActivate = 0x0021;
    private const uint WmTimer = 0x0113;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMouseMove = 0x0200;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmSettingChange = 0x001A;
    private const uint WmCaptureChanged = 0x0215;

    private static readonly IntPtr MaNoActivate = (IntPtr)3;
    private static readonly IntPtr FullscreenPollTimerId = (IntPtr)1;
    private const uint FullscreenPollIntervalMs = 1500;
    private const long TelemetryRedrawIntervalMs = 1000;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const uint MonitorDefaultToNull = 0;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;
    private const int GwlStyle = -16;
    private const uint WsCaption = 0x00C00000u;

    // SHQueryUserNotificationState values
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;

    private readonly string _className = "PGPMintMiniHud_" + Guid.NewGuid().ToString("N")[..8];
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreatedMsg;

    private ID2D1Factory? _d2dFactory;
    private IDWriteFactory? _dwriteFactory;
    private ID2D1DCRenderTarget? _dcRenderTarget;
    private float _cachedFormatScale;
    private IDWriteTextFormat? _rateTextFormat;
    private IDWriteTextFormat? _probTextFormat;
    private IDWriteTextFormat? _etaTextFormat;
    private string _monoFontFamily = "Cascadia Mono";
    private string _uiFontFamily = "Segoe UI";

    private IntPtr _backbufferDc;
    private IntPtr _backbufferDib;
    private IntPtr _backbufferOldObj;
    private int _backbufferWidth;
    private int _backbufferHeight;
    private bool _dcBound;

    private IntPtr _hwnd;
    private int _widthPx;
    private int _heightPx;
    private int _posX;
    private int _posY;
    private bool _hasSavedPos;

    private bool _configured;
    private bool _enabledByUser = true;
    private bool _mainWindowInTray;
    private bool _setupActive;
    private bool _isVisible;
    private bool _hasRenderedSurface;
    private bool _pollTimerRunning;
    private bool _isRecalculating;
    private bool _cachedIsLightTheme;
    private bool _isFullscreenSuppressed;
    private long _lastRenderTick = -TelemetryRedrawIntervalMs;

    private bool _isDragging;
    private int _dragStartMouseX;
    private int _dragStartMouseY;
    private int _dragStartWinX;
    private int _dragStartWinY;
    private bool _dragMoved;

    private MiniHudSnapshot _lastSnapshot = new(CoordinatorState.Idle, "0.00 GH/s", "0.000%", "\u2014", 0.0);
    private string _lastRenderedKey = string.Empty;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? ContextMenuRequested;
    public event EventHandler<(int X, int Y)>? PositionCommitted;

    public MiniHudService()
    {
        _wndProc = WindowProc;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        _cachedIsLightTheme = QuerySystemLightTheme();

        InitializeCpuDirect2D();
        CreateHudWindow();
    }

    private void InitializeCpuDirect2D()
    {
        try
        {
            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory>(D2D1FactoryType.SingleThreaded);
            _dwriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);

            var props = new RenderTargetProperties(
                RenderTargetType.Software,
                new PixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                96f,
                96f,
                RenderTargetUsage.None,
                FeatureLevel.Default);

            _dcRenderTarget = _d2dFactory.CreateDCRenderTarget(props);
            _dcRenderTarget.AntialiasMode = AntialiasMode.PerPrimitive;
            _dcRenderTarget.TextAntialiasMode = D2D1TextAntialiasMode.Grayscale;

            _monoFontFamily = PickInstalledFont(_dwriteFactory, "Cascadia Mono", "Consolas", "Courier New");
            _uiFontFamily = PickInstalledFont(_dwriteFactory, "Segoe UI Variable Text", "Segoe UI", "Microsoft JhengHei UI");
        }
        catch
        {
            DisposeDirect2D();
        }
    }

    private static string PickInstalledFont(IDWriteFactory factory, params string[] candidates)
    {
        try
        {
            using IDWriteFontCollection collection = factory.GetSystemFontCollection(false);
            foreach (string name in candidates)
            {
                if (collection.FindFamilyName(name, out _))
                {
                    return name;
                }
            }
        }
        catch
        {
            // Keep default fallback
        }

        return candidates[^1];
    }

    private void DisposeTextFormats()
    {
        _rateTextFormat?.Dispose();
        _rateTextFormat = null;
        _probTextFormat?.Dispose();
        _probTextFormat = null;
        _etaTextFormat?.Dispose();
        _etaTextFormat = null;
        _cachedFormatScale = 0f;
    }

    private void DisposeDirect2D()
    {
        DisposeTextFormats();
        _dcBound = false;
        _dcRenderTarget?.Dispose();
        _dcRenderTarget = null;
        _dwriteFactory?.Dispose();
        _dwriteFactory = null;
        _d2dFactory?.Dispose();
        _d2dFactory = null;
    }

    private void DisposeBackbuffer()
    {
        _dcBound = false;
        if (_backbufferDc != IntPtr.Zero)
        {
            if (_backbufferOldObj != IntPtr.Zero)
            {
                SelectObject(_backbufferDc, _backbufferOldObj);
                _backbufferOldObj = IntPtr.Zero;
            }

            if (_backbufferDib != IntPtr.Zero)
            {
                DeleteObject(_backbufferDib);
                _backbufferDib = IntPtr.Zero;
            }

            DeleteDC(_backbufferDc);
            _backbufferDc = IntPtr.Zero;
        }
        else if (_backbufferDib != IntPtr.Zero)
        {
            DeleteObject(_backbufferDib);
            _backbufferDib = IntPtr.Zero;
        }

        _backbufferWidth = 0;
        _backbufferHeight = 0;
    }

    public void Configure(bool enabledByUser, int? savedX, int? savedY)
    {
        _configured = true;
        _enabledByUser = enabledByUser;
        if (savedX.HasValue && savedY.HasValue)
        {
            _posX = savedX.Value;
            _posY = savedY.Value;
            _hasSavedPos = true;
        }
        else
        {
            _hasSavedPos = false;
        }

        RecalculateMetricsAndPosition();
        _isFullscreenSuppressed = QueryFullscreenAppActive();
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void SetEnabledByUser(bool enabled)
    {
        if (_enabledByUser == enabled)
        {
            return;
        }

        _enabledByUser = enabled;
        if (enabled)
        {
            _isFullscreenSuppressed = QueryFullscreenAppActive();
        }
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void SetMainWindowInTray(bool inTray)
    {
        if (_mainWindowInTray == inTray)
        {
            return;
        }

        _mainWindowInTray = inTray;
        if (inTray)
        {
            _isFullscreenSuppressed = QueryFullscreenAppActive();
        }
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void SetSetupActive(bool setupActive)
    {
        if (_setupActive == setupActive)
        {
            return;
        }

        _setupActive = setupActive;
        if (!setupActive)
        {
            _isFullscreenSuppressed = QueryFullscreenAppActive();
        }
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void UpdateSnapshot(MiniHudSnapshot snapshot)
    {
        bool bypassThrottle = snapshot.State != _lastSnapshot.State ||
                              snapshot.State is not (CoordinatorState.Running or CoordinatorState.Calibrating);
        _lastSnapshot = snapshot;
        RefreshVisibilityAndRender(forceRedraw: false, bypassThrottle: bypassThrottle);
    }

    private void CreateHudWindow()
    {
        IntPtr hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CsDblClks,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512), // IDC_ARROW
            lpszClassName = _className
        };

        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(
            WsExLayered | WsExTopMost | WsExToolWindow | WsExNoActivate,
            _className,
            "PGPMintMiniHud",
            WsPopup,
            0, 0, LogicalWidth, LogicalHeight,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        RecalculateMetricsAndPosition();
    }

    private void RecalculateMetricsAndPosition()
    {
        if (_hwnd == IntPtr.Zero || _isRecalculating)
        {
            return;
        }

        _isRecalculating = true;
        try
        {
            IntPtr targetMon = IntPtr.Zero;
            bool useSavedPos = false;
            if (_hasSavedPos)
            {
                int probeW = Math.Max(LogicalWidth, _widthPx);
                int probeH = Math.Max(LogicalHeight, _heightPx);
                var probeRect = new RECT { Left = _posX, Top = _posY, Right = _posX + probeW, Bottom = _posY + probeH };
                targetMon = MonitorFromRect(ref probeRect, MonitorDefaultToNull);
                useSavedPos = targetMon != IntPtr.Zero;
            }

            if (targetMon == IntPtr.Zero)
            {
                var origin = new POINT { X = 0, Y = 0 };
                targetMon = MonitorFromPoint(origin, MonitorDefaultToPrimary);
            }

            double scale = GetDpiScaleForMonitor(targetMon);
            _widthPx = Math.Max(160, (int)Math.Round(LogicalWidth * scale));
            _heightPx = Math.Max(28, (int)Math.Round(LogicalHeight * scale));

            if (useSavedPos)
            {
                ClampToMonitorWorkArea(targetMon, ref _posX, ref _posY);
            }
            else
            {
                _hasSavedPos = false;
                DockBottomRightMonitor(targetMon, scale);
            }

            if (!_isVisible)
            {
                SetWindowPos(_hwnd, new IntPtr(-1), _posX, _posY, _widthPx, _heightPx, SwpNoActivate);
            }
            else
            {
                SetWindowPos(_hwnd, new IntPtr(-1), _posX, _posY, 0, 0, SwpNoSize | SwpNoActivate);
            }
        }
        finally
        {
            _isRecalculating = false;
        }
    }

    private void DockBottomRightMonitor(IntPtr hMonitor, double scale)
    {
        if (TryGetMonitorWorkArea(hMonitor, out RECT wa))
        {
            int margin = (int)Math.Round(DockMarginDip * scale);
            _posX = wa.Right - _widthPx - margin;
            _posY = wa.Bottom - _heightPx - margin;
            return;
        }

        var workArea = new RECT();
        if (SystemParametersInfoW(0x0030, 0, ref workArea, 0))
        {
            int margin = (int)Math.Round(DockMarginDip * scale);
            _posX = workArea.Right - _widthPx - margin;
            _posY = workArea.Bottom - _heightPx - margin;
        }
        else
        {
            _posX = 40;
            _posY = 40;
        }
    }

    private void ClampToMonitorWorkArea(IntPtr hMonitor, ref int x, ref int y)
    {
        if (!TryGetMonitorWorkArea(hMonitor, out RECT wa))
        {
            return;
        }

        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - _widthPx));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - _heightPx));
    }

    private static bool TryGetMonitorWorkArea(IntPtr hMonitor, out RECT workArea)
    {
        workArea = default;
        if (hMonitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(hMonitor, ref info))
        {
            return false;
        }

        workArea = info.rcWork;
        return true;
    }

    private double GetDpiScaleForMonitor(IntPtr hMonitor)
    {
        if (hMonitor != IntPtr.Zero)
        {
            try
            {
                if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 && dpiX >= 96)
                {
                    return dpiX / 96.0;
                }
            }
            catch
            {
                // Fallback to per-window DPI
            }
        }

        return GetDpiScale();
    }

    private void SyncFullscreenPollTimer(bool shouldPoll)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (shouldPoll && !_pollTimerRunning)
        {
            if (SetTimer(_hwnd, FullscreenPollTimerId, FullscreenPollIntervalMs, IntPtr.Zero) != IntPtr.Zero)
            {
                _pollTimerRunning = true;
            }
        }
        else if (!shouldPoll && _pollTimerRunning)
        {
            KillTimer(_hwnd, FullscreenPollTimerId);
            _pollTimerRunning = false;
        }
    }

    private void RefreshVisibilityAndRender(bool forceRedraw, bool bypassThrottle = false)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        bool eligible = _configured && _enabledByUser && _mainWindowInTray && !_setupActive;
        SyncFullscreenPollTimer(eligible);

        bool wantVisible = eligible && (_isDragging || !_isFullscreenSuppressed);

        if (!wantVisible)
        {
            if (_isVisible)
            {
                ShowWindow(_hwnd, SwHide);
                _isVisible = false;
            }
            return;
        }

        bool isLight = _cachedIsLightTheme;
        string renderKey = $"{_lastSnapshot.State}|{_lastSnapshot.RateText}|{_lastSnapshot.ProbabilityText}|{_lastSnapshot.EtaText}|{(int)Math.Round(_lastSnapshot.ProgressFraction * 500)}|{_widthPx}x{_heightPx}|{isLight}";

        bool keyChanged = !string.Equals(_lastRenderedKey, renderKey, StringComparison.Ordinal);
        long nowTick = Environment.TickCount64;
        bool throttleAllows = forceRedraw || bypassThrottle || !_isVisible || (nowTick - _lastRenderTick >= TelemetryRedrawIntervalMs);

        if ((forceRedraw || !_isVisible || keyChanged) && throttleAllows)
        {
            if (RenderLayeredSurface(_lastSnapshot, isLight))
            {
                _lastRenderedKey = renderKey;
                _hasRenderedSurface = true;
                _lastRenderTick = nowTick;
            }
            else if (!_hasRenderedSurface)
            {
                return;
            }
        }

        if (!_isVisible && _hasRenderedSurface)
        {
            SetWindowPos(_hwnd, new IntPtr(-1), _posX, _posY, _widthPx, _heightPx, SwpNoActivate);
            ShowWindow(_hwnd, SwShowNoActivate);
            _isVisible = true;
        }
    }

    private bool QueryFullscreenAppActive()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0 &&
                state is QunsRunningD3dFullScreen or QunsPresentationMode)
            {
                return true;
            }

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == _hwnd || !IsWindowVisible(fg))
            {
                return false;
            }

            GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == (uint)Environment.ProcessId)
            {
                return false;
            }

            var classBuf = new StringBuilder(64);
            if (GetClassNameW(fg, classBuf, classBuf.Capacity) > 0)
            {
                string cls = classBuf.ToString();
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                    or "Windows.UI.Core.CoreWindow" or "NotifyIconOverflowWindow")
                {
                    return false;
                }
            }

            uint style = unchecked((uint)GetWindowLongW(fg, GwlStyle));
            if ((style & WsCaption) == WsCaption && IsZoomed(fg))
            {
                return false;
            }

            if (!GetWindowRect(fg, out RECT fgRect))
            {
                return false;
            }

            if (fgRect.Right - fgRect.Left <= 0 || fgRect.Bottom - fgRect.Top <= 0)
            {
                return false;
            }

            IntPtr fgMon = MonitorFromWindow(fg, MonitorDefaultToNull);
            if (fgMon == IntPtr.Zero)
            {
                return false;
            }

            IntPtr hudMon = MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
            if (hudMon != IntPtr.Zero && fgMon != hudMon)
            {
                return false;
            }

            var monInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfoW(fgMon, ref monInfo))
            {
                return false;
            }

            RECT mon = monInfo.rcMonitor;
            return fgRect.Left <= mon.Left &&
                   fgRect.Top <= mon.Top &&
                   fgRect.Right >= mon.Right &&
                   fgRect.Bottom >= mon.Bottom;
        }
        catch
        {
            // Never suppress HUD if shell query fails
        }

        return false;
    }

    private static bool QuerySystemLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val != 0;
            }
        }
        catch { }

        return false;
    }

    private double GetDpiScale()
    {
        try
        {
            uint dpi = GetDpiForWindow(_hwnd);
            return dpi >= 96 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private static Color4 FromArgb(byte a, byte r, byte g, byte b) =>
        new(r / 255f, g / 255f, b / 255f, a / 255f);

    private bool RenderLayeredSurface(MiniHudSnapshot snap, bool isLight)
    {
        if (_hwnd == IntPtr.Zero || _widthPx <= 0 || _heightPx <= 0)
        {
            return false;
        }

        if (_dcRenderTarget is null || _dwriteFactory is null)
        {
            DisposeDirect2D();
            InitializeCpuDirect2D();
            if (_dcRenderTarget is null || _dwriteFactory is null)
            {
                return false;
            }
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool needsRecreate;
            bool ok = TryRenderLayeredSurfaceOnce(snap, isLight, out needsRecreate);
            if (ok)
            {
                return true;
            }

            if (!needsRecreate)
            {
                return false;
            }

            DisposeDirect2D();
            InitializeCpuDirect2D();
            if (_dcRenderTarget is null || _dwriteFactory is null)
            {
                return false;
            }
        }

        return false;
    }

    private bool EnsureBackbuffer(IntPtr screenDc, int width, int height)
    {
        if (_backbufferDc != IntPtr.Zero &&
            _backbufferDib != IntPtr.Zero &&
            _backbufferWidth == width &&
            _backbufferHeight == height)
        {
            return true;
        }

        DisposeBackbuffer();

        IntPtr memDc = CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            return false;
        }

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };

        IntPtr hBitmap = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero)
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            return false;
        }

        IntPtr oldBitmap = SelectObject(memDc, hBitmap);
        if (oldBitmap == IntPtr.Zero)
        {
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            return false;
        }

        _backbufferDc = memDc;
        _backbufferDib = hBitmap;
        _backbufferOldObj = oldBitmap;
        _backbufferWidth = width;
        _backbufferHeight = height;
        _dcBound = false;
        return true;
    }

    private void EnsureTextFormats(float scale)
    {
        if (_dwriteFactory is null)
        {
            return;
        }

        if (_rateTextFormat is not null &&
            _probTextFormat is not null &&
            _etaTextFormat is not null &&
            MathF.Abs(scale - _cachedFormatScale) <= 0.001f)
        {
            return;
        }

        DisposeTextFormats();
        try
        {
            _rateTextFormat = CreateCachedTextFormat(_monoFontFamily, FontWeight.SemiBold, 12.2f * scale, TextAlignment.Leading);
            _probTextFormat = CreateCachedTextFormat(_monoFontFamily, FontWeight.Normal, 11.0f * scale, TextAlignment.Center);
            _etaTextFormat = CreateCachedTextFormat(_uiFontFamily, FontWeight.Medium, 11.2f * scale, TextAlignment.Trailing);
            _cachedFormatScale = scale;
        }
        catch
        {
            DisposeTextFormats();
            throw;
        }
    }

    private IDWriteTextFormat CreateCachedTextFormat(
        string fontFamily,
        FontWeight fontWeight,
        float fontSize,
        TextAlignment alignment)
    {
        IDWriteTextFormat fmt = _dwriteFactory!.CreateTextFormat(
            fontFamily,
            null,
            fontWeight,
            DWriteFontStyle.Normal,
            FontStretch.Normal,
            fontSize,
            string.Empty);

        try
        {
            fmt.TextAlignment = alignment;
            fmt.ParagraphAlignment = ParagraphAlignment.Center;
            fmt.WordWrapping = WordWrapping.NoWrap;

            using IDWriteInlineObject ellipsis = _dwriteFactory.CreateEllipsisTrimmingSign(fmt);
            fmt.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, ellipsis);
            return fmt;
        }
        catch
        {
            fmt.Dispose();
            throw;
        }
    }

    private bool TryRenderLayeredSurfaceOnce(MiniHudSnapshot snap, bool isLight, out bool needsRecreate)
    {
        needsRecreate = false;
        if (_dcRenderTarget is null || _dwriteFactory is null)
        {
            return false;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!EnsureBackbuffer(screenDc, _widthPx, _heightPx))
            {
                return false;
            }

            float s = (float)(_heightPx / (double)LogicalHeight);
            float w = _widthPx;
            float h = _heightPx;
            float radius = 10f * s;

            Color4 bgColor = isLight ? FromArgb(242, 247, 248, 250) : FromArgb(238, 22, 25, 30);
            Color4 borderColor = isLight ? FromArgb(58, 17, 24, 39) : FromArgb(54, 255, 255, 255);
            Color4 primaryTextColor = isLight ? FromArgb(255, 17, 24, 39) : FromArgb(255, 243, 244, 246);
            Color4 mutedTextColor = isLight ? FromArgb(255, 55, 65, 81) : FromArgb(255, 181, 188, 200);
            Color4 trackColor = isLight ? FromArgb(31, 0, 0, 0) : FromArgb(36, 255, 255, 255);
            Color4 accentColor = snap.State switch
            {
                CoordinatorState.Running => FromArgb(255, 16, 185, 129),
                CoordinatorState.Calibrating => FromArgb(255, 245, 158, 11),
                CoordinatorState.Faulted => FromArgb(255, 239, 68, 68),
                _ => FromArgb(255, 107, 114, 128)
            };

            if (!_dcBound)
            {
                _dcRenderTarget.BindDC(_backbufferDc, new RawRect(0, 0, _widthPx, _heightPx));
                _dcBound = true;
            }

            EnsureTextFormats(s);
            _dcRenderTarget.BeginDraw();
            SharpGen.Runtime.Result endHr;
            try
            {
                _dcRenderTarget.Clear(new Color4(0f, 0f, 0f, 0f));

                using ID2D1SolidColorBrush bgBrush = _dcRenderTarget.CreateSolidColorBrush(bgColor);
                using ID2D1SolidColorBrush borderBrush = _dcRenderTarget.CreateSolidColorBrush(borderColor);
                using ID2D1SolidColorBrush primaryBrush = _dcRenderTarget.CreateSolidColorBrush(primaryTextColor);
                using ID2D1SolidColorBrush mutedBrush = _dcRenderTarget.CreateSolidColorBrush(mutedTextColor);
                using ID2D1SolidColorBrush trackBrush = _dcRenderTarget.CreateSolidColorBrush(trackColor);
                using ID2D1SolidColorBrush accentBrush = _dcRenderTarget.CreateSolidColorBrush(accentColor);

                // 1. Crisp per-primitive antialiased rounded pill background + subtle border
                float strokeW = Math.Max(1f, MathF.Round(s));
                float inset = strokeW * 0.5f;
                var pillRect = new RoundedRectangle(
                    new RectangleF(inset, inset, w - strokeW, h - strokeW),
                    radius,
                    radius);
                _dcRenderTarget.FillRoundedRectangle(pillRect, bgBrush);
                _dcRenderTarget.DrawRoundedRectangle(pillRect, borderBrush, strokeW);

                // 2. Smooth status dot on left
                float dotRadius = 3.75f * s;
                float dotCx = 14.75f * s;
                float dotCy = (h - 3f * s) * 0.5f;
                var dotCenter = new Vector2(dotCx, dotCy);
                _dcRenderTarget.FillEllipse(new Ellipse(dotCenter, dotRadius, dotRadius), accentBrush);

                // 3. DirectWrite crisp single-row telemetry: [Rate]  [Prob]  [ETA]
                float contentTop = 1.5f * s;
                float contentH = h - 5.5f * s;
                float rateX = 25.5f * s;
                float rateW = 92f * s;
                float probX = rateX + rateW;
                float probW = 58f * s;
                float etaX = probX + probW;
                float etaW = Math.Max(24f, w - etaX - 10f * s);

                DrawCachedText(snap.RateText, _rateTextFormat, primaryBrush, new Rect(rateX, contentTop, rateW, contentH));
                DrawCachedText(snap.ProbabilityText, _probTextFormat, mutedBrush, new Rect(probX, contentTop, probW, contentH));
                DrawCachedText(snap.EtaText, _etaTextFormat, mutedBrush, new Rect(etaX, contentTop, etaW, contentH));

                // 4. Rounded 2px bottom progress hairline
                float barPadX = 10f * s;
                float barH = Math.Max(2f, 2f * s);
                float barY = h - barH - 2.5f * s;
                float barW = Math.Max(4f, w - 2f * barPadX);
                float barR = barH * 0.5f;

                _dcRenderTarget.FillRoundedRectangle(
                    new RoundedRectangle(new RectangleF(barPadX, barY, barW, barH), barR, barR),
                    trackBrush);

                double clampedFrac = Math.Clamp(snap.ProgressFraction, 0.0, 1.0);
                if (clampedFrac > 0.0)
                {
                    float fillW = Math.Max(barH, (float)(barW * clampedFrac));
                    _dcRenderTarget.FillRoundedRectangle(
                        new RoundedRectangle(new RectangleF(barPadX, barY, fillW, barH), barR, barR),
                        accentBrush);
                }
            }
            finally
            {
                endHr = _dcRenderTarget.EndDraw();
            }

            if (endHr == D2D1ResultCode.RecreateTarget)
            {
                _dcBound = false;
                needsRecreate = true;
                return false;
            }

            if (endHr.Failure)
            {
                return false;
            }

            var ptDst = new POINT { X = _posX, Y = _posY };
            var size = new SIZE { Width = _widthPx, Height = _heightPx };
            var ptSrc = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };

            return UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, _backbufferDc, ref ptSrc, 0, ref blend, UlwAlpha);
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (ex.ResultCode == D2D1ResultCode.RecreateTarget)
        {
            _dcBound = false;
            needsRecreate = true;
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void DrawCachedText(string text, IDWriteTextFormat? format, ID2D1Brush brush, Rect bounds)
    {
        if (string.IsNullOrEmpty(text) || format is null || _dcRenderTarget is null)
        {
            return;
        }

        _dcRenderTarget.DrawText(text, format, bounds, brush, DrawTextOptions.Clip);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            RecalculateMetricsAndPosition();
            RefreshVisibilityAndRender(forceRedraw: true);
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmMouseActivate:
                return MaNoActivate;

            case WmTimer:
                if (wParam == FullscreenPollTimerId && !_isDragging)
                {
                    _isFullscreenSuppressed = QueryFullscreenAppActive();
                    RefreshVisibilityAndRender(forceRedraw: false);
                }
                return IntPtr.Zero;

            case WmLButtonDown:
                if (GetCursorPos(out POINT downPt))
                {
                    _isDragging = true;
                    _dragMoved = false;
                    _dragStartMouseX = downPt.X;
                    _dragStartMouseY = downPt.Y;
                    _dragStartWinX = _posX;
                    _dragStartWinY = _posY;
                    SetCapture(hwnd);
                }
                return IntPtr.Zero;

            case WmMouseMove:
                if (_isDragging && GetCursorPos(out POINT movePt))
                {
                    int dx = movePt.X - _dragStartMouseX;
                    int dy = movePt.Y - _dragStartMouseY;
                    if (Math.Abs(dx) >= 3 || Math.Abs(dy) >= 3)
                    {
                        _dragMoved = true;
                    }

                    if (_dragMoved)
                    {
                        _posX = _dragStartWinX + dx;
                        _posY = _dragStartWinY + dy;
                        SetWindowPos(hwnd, IntPtr.Zero, _posX, _posY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
                    }
                }
                return IntPtr.Zero;

            case WmLButtonUp:
                if (_isDragging)
                {
                    _isDragging = false;
                    ReleaseCapture();
                    FinishDrag(hwnd);
                }
                return IntPtr.Zero;

            case WmCaptureChanged:
                if (_isDragging)
                {
                    _isDragging = false;
                    FinishDrag(hwnd);
                }
                return IntPtr.Zero;

            case WmLButtonDblClk:
                if (_isDragging)
                {
                    _isDragging = false;
                    ReleaseCapture();
                }
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;

            case WmRButtonUp:
                if (!_isDragging)
                {
                    ContextMenuRequested?.Invoke(this, EventArgs.Empty);
                }
                return IntPtr.Zero;

            case WmSettingChange:
                _cachedIsLightTheme = QuerySystemLightTheme();
                if (!_isDragging)
                {
                    RecalculateMetricsAndPosition();
                    RefreshVisibilityAndRender(forceRedraw: true);
                }
                return IntPtr.Zero;

            case WmDisplayChange:
            case WmDpiChanged:
                if (!_isDragging)
                {
                    RecalculateMetricsAndPosition();
                    RefreshVisibilityAndRender(forceRedraw: true);
                }
                return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void FinishDrag(IntPtr hwnd)
    {
        if (!_dragMoved)
        {
            return;
        }

        _dragMoved = false;
        var probeRect = new RECT { Left = _posX, Top = _posY, Right = _posX + _widthPx, Bottom = _posY + _heightPx };
        IntPtr targetMon = MonitorFromRect(ref probeRect, MonitorDefaultToNearest);

        double newScale = GetDpiScaleForMonitor(targetMon);
        int newW = Math.Max(160, (int)Math.Round(LogicalWidth * newScale));
        int newH = Math.Max(28, (int)Math.Round(LogicalHeight * newScale));
        bool sizeChanged = newW != _widthPx || newH != _heightPx;
        _widthPx = newW;
        _heightPx = newH;

        ClampToMonitorWorkArea(targetMon, ref _posX, ref _posY);
        _hasSavedPos = true;

        if (sizeChanged)
        {
            RefreshVisibilityAndRender(forceRedraw: true);
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        else
        {
            SetWindowPos(hwnd, new IntPtr(-1), _posX, _posY, 0, 0, SwpNoSize | SwpNoActivate);
        }

        PositionCommitted?.Invoke(this, (_posX, _posY));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            if (_pollTimerRunning)
            {
                KillTimer(_hwnd, FullscreenPollTimerId);
                _pollTimerRunning = false;
            }
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClassW(_className, GetModuleHandleW(null));
        DisposeDirect2D();
        DisposeBackbuffer();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

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

    // User32 / Gdi32 / Shcore / Shell32 / Kernel32
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string lpString);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int pquns);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
