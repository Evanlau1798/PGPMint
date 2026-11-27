using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PGPMint.App.Services;
using PGPMint.App.ViewModels;
using PGPMint.Core.Storage;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace PGPMint.App;

public sealed partial class SettingsWindow : Window
{
    private const uint WmSetIcon = 0x0080;
    private const int SwRestore = 9;
    private const int LogicalDefaultWidth = 520;
    private const int LogicalDefaultHeight = 560;
    private const int LogicalMinWidth = 380;
    private const int LogicalMinHeight = 420;
    private const double TwoColumnBreakpointDip = 460.0;

    private readonly IntPtr _hwnd;
    private readonly TrayIconService _trayIcon;
    private readonly WindowMinSizeHelper _minSizeHelper;
    private bool _isClosed;
    private bool _syncingLangCombo;
    private bool _syncingMaxTempCombo;
    private bool _centeredOnce;
    private int _lastDesiredLogicalHeight;
    private int _currentPrefColumns;

    public MainViewModel ViewModel { get; }

    public SettingsWindow(MainViewModel viewModel, TrayIconService trayIcon)
    {
        ViewModel = viewModel;
        _trayIcon = trayIcon;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _minSizeHelper = new WindowMinSizeHelper(_hwnd, LogicalMinWidth, LogicalMinHeight);
        ConfigureWindowChrome();

        LanguageComboBox.ItemsSource = ViewModel.LanguageOptions;
        LanguageComboBox.Loaded += (_, _) => SyncLanguageCombo();
        MaxGpuTempComboBox.ItemsSource = ViewModel.MaxGpuTempOptions;
        MaxGpuTempComboBox.Loaded += (_, _) => SyncMaxGpuTempCombo();

        ViewModel.SyncDraftFromActive();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += OnSettingsWindowClosed;
        double clientWidthDip = AppWindow.ClientSize.Width / WindowMinSizeHelper.GetDpiScale(_hwnd);
        ApplyResponsivePreferencesLayout(clientWidthDip);
        UpdateVisualStates();

        // Fit the first visible frame using the same content and width as the settings layout.
        Bindings.Initialize();
        SettingsContentPanel.Measure(new Windows.Foundation.Size(clientWidthDip, double.PositiveInfinity));
        ResizeToContent(SettingsContentPanel.DesiredSize.Height);
    }

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _minSizeHelper.Dispose();
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
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

        WindowMinSizeHelper.CenterAndResizeOnScreen(
            AppWindow,
            _hwnd,
            LogicalDefaultWidth,
            LogicalDefaultHeight);
    }

    private void OnSettingsContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeToContent(e.NewSize.Height);
    }

    private void ResizeToContent(double contentHeight)
    {
        if (_isClosed || contentHeight <= 0)
        {
            return;
        }

        int desiredClientLogicalHeight = (int)Math.Ceiling(contentHeight) + 38;
        if (_centeredOnce && Math.Abs(desiredClientLogicalHeight - _lastDesiredLogicalHeight) <= 4)
        {
            return;
        }

        bool centerNow = !_centeredOnce;
        _centeredOnce = true;
        _lastDesiredLogicalHeight = desiredClientLogicalHeight;

        WindowMinSizeHelper.ResizeClientHeightClampedToWorkArea(
            AppWindow,
            _hwnd,
            desiredClientLogicalHeight,
            LogicalMinHeight,
            centerOnMonitor: centerNow);
    }

    private void OnRootLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsivePreferencesLayout(e.NewSize.Width);
    }

    private void ApplyResponsivePreferencesLayout(double widthDip)
    {
        int cols = widthDip >= TwoColumnBreakpointDip ? 2 : 1;
        if (cols == _currentPrefColumns)
        {
            return;
        }

        _currentPrefColumns = cols;
        PreferencesGrid.ColumnDefinitions.Clear();
        PreferencesGrid.RowDefinitions.Clear();

        for (int c = 0; c < cols; c++)
        {
            PreferencesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        ToggleSwitch[] switches = [PrefAutoPause, PrefMinimizeToTray, PrefShowMiniHud, PrefStartWithWindows];
        int rows = (switches.Length + cols - 1) / cols;
        for (int r = 0; r < rows; r++)
        {
            PreferencesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < switches.Length; i++)
        {
            Grid.SetRow(switches[i], i / cols);
            Grid.SetColumn(switches[i], i % cols);
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
            or nameof(MainViewModel.CanEditTarget)
            or nameof(MainViewModel.SelectedLanguageCode)
            or nameof(MainViewModel.SelectedMaxGpuTempLimitValue)
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
        SyncLanguageCombo();
        SyncMaxGpuTempCombo();

        Title = $"PGPMint - {ViewModel.Loc.Settings}";
    }

    private void SyncLanguageCombo()
    {
        _syncingLangCombo = true;
        try
        {
            LanguageComboBox.SelectedValue = ViewModel.SelectedLanguageCode;
        }
        finally
        {
            _syncingLangCombo = false;
        }
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLangCombo)
        {
            return;
        }

        if (LanguageComboBox.SelectedValue is string code && !string.IsNullOrWhiteSpace(code))
        {
            ViewModel.SetLanguage(code);
        }
    }

    private void SyncMaxGpuTempCombo()
    {
        _syncingMaxTempCombo = true;
        try
        {
            MaxGpuTempComboBox.SelectedValue = ViewModel.SelectedMaxGpuTempLimitValue;
        }
        finally
        {
            _syncingMaxTempCombo = false;
        }
    }

    private void OnMaxGpuTempSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingMaxTempCombo)
        {
            return;
        }

        if (MaxGpuTempComboBox.SelectedValue is int limitValue)
        {
            ViewModel.SetMaxGpuTempLimit(limitValue);
        }
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

    private async void OnRecalibrateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.RecalibrateAsync();
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                ViewModel.ReportError(ex.Message);
            }
        }
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

    private async void OnExportSyncClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool hasMatches = ViewModel.HasMatches;
            bool includeKeys = false;
            string passphrase = string.Empty;

            if (hasMatches)
            {
                var includeCheck = new CheckBox
                {
                    Content = ViewModel.Loc.SyncIncludeKeysCheck,
                    IsChecked = true
                };
                var pwBox = new PasswordBox
                {
                    PlaceholderText = ViewModel.Loc.SyncBundlePassphrasePrompt
                };
                includeCheck.Checked += (_, _) => pwBox.IsEnabled = true;
                includeCheck.Unchecked += (_, _) => pwBox.IsEnabled = false;

                var body = new StackPanel { Spacing = 10 };
                body.Children.Add(includeCheck);
                body.Children.Add(pwBox);

                var dlg = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = ViewModel.Loc.SyncExportButton,
                    Content = body,
                    PrimaryButtonText = ViewModel.Loc.Save,
                    CloseButtonText = ViewModel.Loc.Cancel,
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                includeKeys = includeCheck.IsChecked == true;
                passphrase = pwBox.Password;
                pwBox.Password = string.Empty;
                if (includeKeys && string.IsNullOrEmpty(passphrase))
                {
                    ViewModel.ReportError(ViewModel.Loc.SyncBundlePassphrasePrompt);
                    return;
                }
            }

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "pgpmint-sync"
            };
            picker.FileTypeChoices.Add("PGPMint Sync Bundle", [".pgpmint-sync"]);
            InitializeWithWindow.Initialize(picker, _hwnd);
            Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            ViewModel.ExportSyncBundle(file.Path, includeKeys, passphrase);
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                ViewModel.ReportError(ex.Message);
            }
        }
    }

    private async void OnImportSyncClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".pgpmint-sync");
            InitializeWithWindow.Initialize(picker, _hwnd);
            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            SyncBundleInfo info = SyncBundleService.Inspect(file.Path);
            string passphrase = string.Empty;
            if (info.HasEncryptedKeys && info.KeyCount > 0)
            {
                var pwBox = new PasswordBox
                {
                    PlaceholderText = ViewModel.Loc.SyncBundlePassphrasePrompt
                };
                var dlg = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = ViewModel.Loc.SyncImportButton,
                    Content = pwBox,
                    PrimaryButtonText = ViewModel.Loc.Apply,
                    CloseButtonText = ViewModel.Loc.Cancel,
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }

                passphrase = pwBox.Password;
                pwBox.Password = string.Empty;
                if (string.IsNullOrEmpty(passphrase))
                {
                    return;
                }
            }

            ViewModel.ImportSyncBundle(file.Path, passphrase);
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                ViewModel.ReportError(ex.Message);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
