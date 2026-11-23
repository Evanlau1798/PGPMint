using System.Runtime.InteropServices;
using Microsoft.Win32;
using PGPMint.Core.Search;

namespace PGPMint.App.Services;

public sealed record MiniHudSnapshot(
    CoordinatorState State,
    string RateText,
    string ProbabilityText,
    string EtaText,
    double ProgressFraction);

/// <summary>
/// Pure CPU Win32 layered floating HUD drawn via built-in gdiplus.dll and UpdateLayeredWindow.
/// Uses zero WinUI 3 / DirectComposition / D3D resources so background GPU search stays at full speed.
/// </summary>
public sealed class MiniHudService : IDisposable
{
    private const int LogicalWidth = 264;
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

    // GDI+ constants
    private const int PixelFormat32bppPArgb = 0x000E200B;
    private const int SmoothingModeAntiAlias = 4;
    private const int TextRenderingHintAntiAliasGridFit = 3;
    private const int UnitPixel = 2;
    private const int FontStyleRegular = 0;
    private const int FontStyleBold = 1;
    private const int StringAlignmentNear = 0;
    private const int StringAlignmentCenter = 1;
    private const int StringAlignmentFar = 2;
    private const int StringTrimmingEllipsisCharacter = 3;

    // SHQueryUserNotificationState values
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;

    private readonly string _className = "PGPMintMiniHud_" + Guid.NewGuid().ToString("N")[..8];
    private readonly WndProcDelegate _wndProc;
    private readonly uint _taskbarCreatedMsg;

    private IntPtr _gdipToken;
    private bool _gdipStarted;
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
    private bool _pollTimerRunning;
    private bool _isRecalculating;

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

        var startup = new GdiplusStartupInput { GdiplusVersion = 1 };
        _gdipStarted = GdiplusStartup(out _gdipToken, ref startup, IntPtr.Zero) == 0;

        CreateHudWindow();
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
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void SetEnabledByUser(bool enabled)
    {
        if (_enabledByUser == enabled)
        {
            return;
        }

        _enabledByUser = enabled;
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void SetMainWindowInTray(bool inTray)
    {
        if (_mainWindowInTray == inTray)
        {
            return;
        }

        _mainWindowInTray = inTray;
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void SetSetupActive(bool setupActive)
    {
        if (_setupActive == setupActive)
        {
            return;
        }

        _setupActive = setupActive;
        RefreshVisibilityAndRender(forceRedraw: true);
    }

    public void UpdateSnapshot(MiniHudSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        RefreshVisibilityAndRender(forceRedraw: false);
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
                // Update position/Z-order only; UpdateLayeredWindow in RefreshVisibilityAndRender atomically updates size + pixels.
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
        if (SystemParametersInfoW(0x0030, 0, ref workArea, 0)) // SPI_GETWORKAREA fallback
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
                // Fallback to per-window DPI on systems without shcore.dll GetDpiForMonitor
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

    private void RefreshVisibilityAndRender(bool forceRedraw)
    {
        if (_disposed || _hwnd == IntPtr.Zero)
        {
            return;
        }

        bool eligible = _configured && _enabledByUser && _mainWindowInTray && !_setupActive;
        SyncFullscreenPollTimer(eligible);

        bool wantVisible = eligible && (_isDragging || !IsFullscreenAppActive());

        if (!wantVisible)
        {
            if (_isVisible)
            {
                ShowWindow(_hwnd, SwHide);
                _isVisible = false;
            }
            return;
        }

        bool isLight = IsSystemLightTheme();
        string renderKey = $"{_lastSnapshot.State}|{_lastSnapshot.RateText}|{_lastSnapshot.ProbabilityText}|{_lastSnapshot.EtaText}|{(int)Math.Round(_lastSnapshot.ProgressFraction * 500)}|{_widthPx}x{_heightPx}|{isLight}";

        if (forceRedraw || !_isVisible || !string.Equals(_lastRenderedKey, renderKey, StringComparison.Ordinal))
        {
            RenderLayeredSurface(_lastSnapshot, isLight);
            _lastRenderedKey = renderKey;
        }

        if (!_isVisible)
        {
            SetWindowPos(_hwnd, new IntPtr(-1), _posX, _posY, _widthPx, _heightPx, SwpNoActivate);
            ShowWindow(_hwnd, SwShowNoActivate);
            _isVisible = true;
        }
    }

    private bool IsFullscreenAppActive()
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

            var classBuf = new System.Text.StringBuilder(64);
            if (GetClassNameW(fg, classBuf, classBuf.Capacity) > 0)
            {
                string cls = classBuf.ToString();
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                    or "Windows.UI.Core.CoreWindow" or "NotifyIconOverflowWindow")
                {
                    return false;
                }
            }

            // Exclude normal maximized desktop windows (which have WS_CAPTION and overlap rcMonitor
            // when taskbar is auto-hidden or on secondary monitors without a taskbar).
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

    private static bool IsSystemLightTheme()
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

    private void RenderLayeredSurface(MiniHudSnapshot snap, bool isLight)
    {
        if (!_gdipStarted || _hwnd == IntPtr.Zero || _widthPx <= 0 || _heightPx <= 0)
        {
            return;
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return;
        }

        IntPtr memDc = CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }

        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;
        IntPtr gpBitmap = IntPtr.Zero;
        IntPtr gpGraphics = IntPtr.Zero;

        try
        {
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = _widthPx,
                biHeight = -_heightPx, // top-down DIB matches GDI+ scan0 layout
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            };

            hBitmap = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bitsPtr, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bitsPtr == IntPtr.Zero)
            {
                return;
            }

            oldBitmap = SelectObject(memDc, hBitmap);

            int stride = _widthPx * 4;
            if (GdipCreateBitmapFromScan0(_widthPx, _heightPx, stride, PixelFormat32bppPArgb, bitsPtr, out gpBitmap) != 0)
            {
                return;
            }

            if (GdipGetImageGraphicsContext(gpBitmap, out gpGraphics) != 0)
            {
                return;
            }

            GdipSetSmoothingMode(gpGraphics, SmoothingModeAntiAlias);
            GdipSetTextRenderingHint(gpGraphics, TextRenderingHintAntiAliasGridFit);
            GdipGraphicsClear(gpGraphics, 0x00000000u);

            float s = (float)(_heightPx / (double)LogicalHeight);
            float w = _widthPx;
            float h = _heightPx;
            float radius = 10f * s;

            // Adaptive Dark / Light translucent pill palette
            uint bgArgb = isLight ? 0xECF7F8FAu : 0xE816191Eu;
            uint borderArgb = isLight ? 0x3A111827u : 0x36FFFFFFu;
            uint primaryTextArgb = isLight ? 0xFF111827u : 0xFFF3F4F6u;
            uint mutedTextArgb = isLight ? 0xFF4B5563u : 0xFF9CA3AFu;
            uint trackArgb = isLight ? 0x1F000000u : 0x22FFFFFFu;
            uint accentArgb = snap.State switch
            {
                CoordinatorState.Running => 0xFF10B981u,
                CoordinatorState.QuickProbing or CoordinatorState.Calibrating => 0xFFF59E0Bu,
                CoordinatorState.Faulted => 0xFFEF4444u,
                _ => 0xFF6B7280u
            };

            // 1. Rounded pill background + subtle 1px border
            DrawRoundedPill(gpGraphics, 0.5f, 0.5f, w - 1f, h - 1f, radius, bgArgb, borderArgb, Math.Max(1f, s));

            // 2. Status dot on left
            float dotSize = 7.5f * s;
            float dotX = 11f * s;
            float dotY = ((h - 3f * s) - dotSize) * 0.5f;
            FillEllipse(gpGraphics, accentArgb, dotX, dotY, dotSize, dotSize);

            // 3. Single-row telemetry: [Rate (bold)]  [Prob]  [ETA (right-aligned)]
            float contentTop = 2f * s;
            float contentHeight = h - 6f * s;
            float rateX = dotX + dotSize + 7f * s;
            float rateW = 88f * s;
            float probX = rateX + rateW;
            float probW = 58f * s;
            float etaX = probX + probW;
            float etaW = Math.Max(24f, w - etaX - 10f * s);

            DrawMonoText(gpGraphics, snap.RateText, 12.5f * s, FontStyleBold, primaryTextArgb,
                rateX, contentTop, rateW, contentHeight, StringAlignmentNear);
            DrawMonoText(gpGraphics, snap.ProbabilityText, 11.0f * s, FontStyleRegular, mutedTextArgb,
                probX, contentTop, probW, contentHeight, StringAlignmentCenter);
            DrawMonoText(gpGraphics, snap.EtaText, 11.0f * s, FontStyleRegular, mutedTextArgb,
                etaX, contentTop, etaW, contentHeight, StringAlignmentFar);

            // 4. Crisp 2px bottom progress hairline
            float barPadX = 10f * s;
            float barH = Math.Max(2f, 2f * s);
            float barY = h - barH - 2.5f * s;
            float barW = Math.Max(4f, w - 2f * barPadX);
            FillRect(gpGraphics, trackArgb, barPadX, barY, barW, barH);

            double clampedFrac = Math.Clamp(snap.ProgressFraction, 0.0, 1.0);
            if (clampedFrac > 0.0)
            {
                float fillW = Math.Max(barH, (float)(barW * clampedFrac));
                FillRect(gpGraphics, accentArgb, barPadX, barY, fillW, barH);
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

            UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (gpGraphics != IntPtr.Zero) GdipDeleteGraphics(gpGraphics);
            if (gpBitmap != IntPtr.Zero) GdipDisposeImage(gpBitmap);
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void DrawRoundedPill(
        IntPtr graphics, float x, float y, float width, float height, float radius,
        uint fillArgb, uint strokeArgb, float strokeWidth)
    {
        if (GdipCreatePath(0, out IntPtr path) != 0)
        {
            return;
        }

        IntPtr brush = IntPtr.Zero;
        IntPtr pen = IntPtr.Zero;
        try
        {
            float d = radius * 2f;
            GdipAddPathArc(path, x, y, d, d, 180f, 90f);
            GdipAddPathArc(path, x + width - d, y, d, d, 270f, 90f);
            GdipAddPathArc(path, x + width - d, y + height - d, d, d, 0f, 90f);
            GdipAddPathArc(path, x, y + height - d, d, d, 90f, 90f);
            GdipClosePathFigure(path);

            if (GdipCreateSolidFill(fillArgb, out brush) == 0)
            {
                GdipFillPath(graphics, brush, path);
            }

            if (GdipCreatePen1(strokeArgb, strokeWidth, UnitPixel, out pen) == 0)
            {
                GdipDrawPath(graphics, pen, path);
            }
        }
        finally
        {
            if (pen != IntPtr.Zero) GdipDeletePen(pen);
            if (brush != IntPtr.Zero) GdipDeleteBrush(brush);
            GdipDeletePath(path);
        }
    }

    private static void FillEllipse(IntPtr graphics, uint argb, float x, float y, float w, float h)
    {
        if (GdipCreateSolidFill(argb, out IntPtr brush) != 0) return;
        try { GdipFillEllipse(graphics, brush, x, y, w, h); }
        finally { GdipDeleteBrush(brush); }
    }

    private static void FillRect(IntPtr graphics, uint argb, float x, float y, float w, float h)
    {
        if (GdipCreateSolidFill(argb, out IntPtr brush) != 0) return;
        try { GdipFillRectangle(graphics, brush, x, y, w, h); }
        finally { GdipDeleteBrush(brush); }
    }

    private static void DrawMonoText(
        IntPtr graphics, string text, float pxSize, int style, uint argb,
        float x, float y, float width, float height, int horizontalAlign)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        IntPtr family = IntPtr.Zero;
        IntPtr font = IntPtr.Zero;
        IntPtr format = IntPtr.Zero;
        IntPtr brush = IntPtr.Zero;
        try
        {
            if (GdipCreateFontFamilyFromName("Cascadia Mono", IntPtr.Zero, out family) != 0 &&
                GdipCreateFontFamilyFromName("Consolas", IntPtr.Zero, out family) != 0)
            {
                GdipGetGenericFontFamilyMonospace(out family);
            }

            if (family == IntPtr.Zero || GdipCreateFont(family, pxSize, style, UnitPixel, out font) != 0)
            {
                return;
            }

            if (GdipCreateStringFormat(0x1000, 0, out format) != 0) // StringFormatFlagsNoWrap
            {
                return;
            }

            GdipSetStringFormatAlign(format, horizontalAlign);
            GdipSetStringFormatLineAlign(format, StringAlignmentCenter);
            GdipSetStringFormatTrimming(format, StringTrimmingEllipsisCharacter);

            if (GdipCreateSolidFill(argb, out brush) != 0)
            {
                return;
            }

            var rect = new RECTF { X = x, Y = y, Width = width, Height = height };
            GdipDrawString(graphics, text, text.Length, font, ref rect, format, brush);
        }
        finally
        {
            if (brush != IntPtr.Zero) GdipDeleteBrush(brush);
            if (format != IntPtr.Zero) GdipDeleteStringFormat(format);
            if (font != IntPtr.Zero) GdipDeleteFont(font);
            if (family != IntPtr.Zero) GdipDeleteFontFamily(family);
        }
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

            case WmDisplayChange:
            case WmDpiChanged:
            case WmSettingChange:
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

        if (_gdipStarted && _gdipToken != IntPtr.Zero)
        {
            GdiplusShutdown(_gdipToken);
            _gdipStarted = false;
            _gdipToken = IntPtr.Zero;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECTF { public float X; public float Y; public float Width; public float Height; }

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

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        [MarshalAs(UnmanagedType.Bool)] public bool SuppressBackgroundThread;
        [MarshalAs(UnmanagedType.Bool)] public bool SuppressExternalCodecs;
    }

    // User32 / Gdi32 / Shell32 / Kernel32
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int pquns);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // Built-in GDI+ flat C API (gdiplus.dll)
    [DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out IntPtr token, ref GdiplusStartupInput input, IntPtr output);
    [DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(IntPtr token);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);
    [DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(IntPtr image);
    [DllImport("gdiplus.dll")] private static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport("gdiplus.dll")] private static extern int GdipSetSmoothingMode(IntPtr graphics, int smoothingMode);
    [DllImport("gdiplus.dll")] private static extern int GdipSetTextRenderingHint(IntPtr graphics, int mode);
    [DllImport("gdiplus.dll")] private static extern int GdipGraphicsClear(IntPtr graphics, uint color);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePath(int brushMode, out IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePath(IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipAddPathArc(IntPtr path, float x, float y, float width, float height, float startAngle, float sweepAngle);
    [DllImport("gdiplus.dll")] private static extern int GdipClosePathFigure(IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateSolidFill(uint color, out IntPtr brush);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport("gdiplus.dll")] private static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);
    [DllImport("gdiplus.dll")] private static extern int GdipFillEllipse(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);
    [DllImport("gdiplus.dll")] private static extern int GdipFillRectangle(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);
    [DllImport("gdiplus.dll")] private static extern int GdipCreatePen1(uint color, float width, int unit, out IntPtr pen);
    [DllImport("gdiplus.dll")] private static extern int GdipDeletePen(IntPtr pen);
    [DllImport("gdiplus.dll")] private static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipCreateFontFamilyFromName(string name, IntPtr fontCollection, out IntPtr fontFamily);
    [DllImport("gdiplus.dll")] private static extern int GdipGetGenericFontFamilyMonospace(out IntPtr nativeFamily);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFontFamily(IntPtr fontFamily);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateFont(IntPtr fontFamily, float emSize, int style, int unit, out IntPtr font);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteFont(IntPtr font);
    [DllImport("gdiplus.dll")] private static extern int GdipCreateStringFormat(int formatAttributes, ushort language, out IntPtr format);
    [DllImport("gdiplus.dll")] private static extern int GdipDeleteStringFormat(IntPtr format);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatAlign(IntPtr format, int align);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatLineAlign(IntPtr format, int align);
    [DllImport("gdiplus.dll")] private static extern int GdipSetStringFormatTrimming(IntPtr format, int trimming);
    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)] private static extern int GdipDrawString(IntPtr graphics, string str, int length, IntPtr font, ref RECTF layoutRect, IntPtr stringFormat, IntPtr brush);
}
