using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PGPMint.App.Services;
using PGPMint.App.ViewModels;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace PGPMint.App;

public sealed partial class SettingsWindow : Window
{
    private const uint WmSetIcon = 0x0080;
    private const int SwRestore = 9;
    private const int LogicalDefaultWidth = 460;
    private const int LogicalDefaultHeight = 620;
    private const int LogicalMinWidth = 400;
    private const int LogicalMinHeight = 500;

    private readonly IntPtr _hwnd;
    private readonly TrayIconService _trayIcon;
    private bool _isClosed;

    public MainViewModel ViewModel { get; }

    public SettingsWindow(MainViewModel viewModel, TrayIconService trayIcon)
    {
        ViewModel = viewModel;
        _trayIcon = trayIcon;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        ConfigureWindowChrome();

        ViewModel.SyncDraftFromActive();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnSettingsWindowClosed;
        UpdateVisualStates();
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        AppWindow.Changed -= OnAppWindowChanged;
        Bindings.StopTracking();
    }

    private void ConfigureWindowChrome()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Fallback on Windows 10
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);

        if (_trayIcon.AppIconHandle != IntPtr.Zero)
        {
            SendMessageW(_hwnd, WmSetIcon, IntPtr.Zero, _trayIcon.AppIconHandle);
            SendMessageW(_hwnd, WmSetIcon, (IntPtr)1, _trayIcon.AppIconHandle);
        }

        double scale = GetDpiScale();
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalDefaultWidth * scale),
            (int)Math.Round(LogicalDefaultHeight * scale)));
        AppWindow.Changed += OnAppWindowChanged;
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

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        double scale = GetDpiScale();
        int minW = (int)Math.Round(LogicalMinWidth * scale);
        int minH = (int)Math.Round(LogicalMinHeight * scale);
        int w = Math.Max(minW, sender.Size.Width);
        int h = Math.Max(minH, sender.Size.Height);
        if (w != sender.Size.Width || h != sender.Size.Height)
        {
            sender.Resize(new SizeInt32(w, h));
        }
    }

    public void FocusWindow()
    {
        AppWindow.Show();
        ShowWindow(_hwnd, SwRestore);
        SetForegroundWindow(_hwnd);
        Activate();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasDraftError)
            or nameof(MainViewModel.DraftEtaSeverityLevel)
            or nameof(MainViewModel.CanEditTarget)
            or nameof(MainViewModel.SelectedLanguage)
            or "")
        {
            UpdateVisualStates();
        }
    }

    private void UpdateVisualStates()
    {
        SettingsValidationErrorText.Visibility = ViewModel.HasDraftError ? Visibility.Visible : Visibility.Collapsed;
        StopToEditNotice.Visibility = !ViewModel.CanEditTarget ? Visibility.Visible : Visibility.Collapsed;

        ApplyTextBoxValidationVisual(SettingsInputFrame, SettingsTargetInputBox, ViewModel.HasDraftError);

        if (ViewModel.DraftEtaSeverityLevel == 2)
        {
            SettingsEtaPill.Background = new SolidColorBrush(Color.FromArgb(44, 239, 68, 68));
        }
        else if (ViewModel.DraftEtaSeverityLevel == 1)
        {
            SettingsEtaPill.Background = new SolidColorBrush(Color.FromArgb(44, 245, 158, 11));
        }
        else
        {
            SettingsEtaPill.ClearValue(Border.BackgroundProperty);
        }

        LangZhButton.IsChecked = ViewModel.Loc.IsChinese;
        LangEnButton.IsChecked = !ViewModel.Loc.IsChinese;
        Title = $"PGPMint - {ViewModel.Loc.Settings}";
    }

    private static void ApplyTextBoxValidationVisual(Border frame, TextBox textBox, bool hasError)
    {
        if (hasError)
        {
            var red = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
            frame.BorderBrush = red;
            frame.Background = new SolidColorBrush(Color.FromArgb(28, 239, 68, 68));
            textBox.BorderBrush = red;
            textBox.Foreground = red;
            SetInnerTextBoxForeground(textBox, red);
        }
        else
        {
            frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            frame.Background = null;
            textBox.ClearValue(Control.BorderBrushProperty);
            textBox.ClearValue(Control.ForegroundProperty);
            SetInnerTextBoxForeground(textBox, null);
        }
    }

    private static void SetInnerTextBoxForeground(DependencyObject root, Brush? brush)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv && sv.Name == "ContentElement")
            {
                if (brush is not null) sv.Foreground = brush;
                else sv.ClearValue(Control.ForegroundProperty);
                return;
            }
            SetInnerTextBoxForeground(child, brush);
        }
    }

    private void OnApplyTargetClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyDraftTargetFromSettings();
        UpdateVisualStates();
    }

    private async void OnGpuToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string deviceKey)
        {
            return;
        }

        try
        {
            bool actualState = await ViewModel.ToggleGpuAsync(deviceKey, cb.IsChecked == true);
            if (!_isClosed)
            {
                cb.IsChecked = actualState;
            }
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                ViewModel.ReportError(ex.Message);
            }
        }
    }

    private async void OnRecalibrateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.Recalibrate30sAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
    }

    private void OnLangZhClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SetLanguage("zh-Hant");
        UpdateVisualStates();
    }

    private void OnLangEnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SetLanguage("en-US");
        UpdateVisualStates();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
