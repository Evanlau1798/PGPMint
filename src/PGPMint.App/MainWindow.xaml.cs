using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PGPMint.App.Services;
using PGPMint.App.ViewModels;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace PGPMint.App;

public sealed partial class MainWindow : Window
{
    private const uint WmSetIcon = 0x0080;
    private const int SwRestore = 9;
    private const int LogicalDefaultWidth = 460;
    private const int LogicalBaseDashboardHeight = 434;
    private const int LogicalMinWidth = 380;
    private const int LogicalMinHeight = 416;
    private const int MaxAutoStretchMatches = 5;

    private readonly TrayIconService _trayIcon;
    private readonly Func<Task> _quitCallback;
    private readonly Action _openSettingsCallback;
    private readonly IntPtr _hwnd;
    private readonly WindowMinSizeHelper _minSizeHelper;
    private bool _allowClose;
    private bool _isDialogOpen;
    private bool _isStartingSetup;
    private bool _syncingLangCombo;
    private bool? _lastSizedSetupMode;
    private int _lastSizedVisibleMatchRows = -1;
    private double _lastSetupDesiredClientHeight;

    public MainViewModel ViewModel { get; }

    public MainWindow(
        MainViewModel viewModel,
        TrayIconService trayIcon,
        Func<Task> quitCallback,
        Action openSettingsCallback)
    {
        ViewModel = viewModel;
        _trayIcon = trayIcon;
        _quitCallback = quitCallback;
        _openSettingsCallback = openSettingsCallback;

        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _minSizeHelper = new WindowMinSizeHelper(_hwnd, LogicalMinWidth, LogicalMinHeight);
        ConfigureWindowChrome();

        SetupLanguageComboBox.ItemsSource = ViewModel.LanguageOptions;
        SetupLanguageComboBox.Loaded += (_, _) => SyncSetupLanguageCombo();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateVisualStates();
    }

    private void ConfigureWindowChrome()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Graceful fallback on Windows 10 systems without Mica
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
            LogicalBaseDashboardHeight);
        AppWindow.Closing += OnAppWindowClosing;
        Closed += (_, _) => _minSizeHelper.Dispose();
    }

    public void HideToTray()
    {
        AppWindow.Hide();
        ViewModel.SetMainWindowInTray(true);
    }

    public void ShowAndActivate()
    {
        ViewModel.SetMainWindowInTray(false);
        AppWindow.Show();
        ShowWindow(_hwnd, SwRestore);
        SetForegroundWindow(_hwnd);
        Activate();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        if (ViewModel.MinimizeToTrayOnClose && !ViewModel.ShowSetupWizard)
        {
            args.Cancel = true;
            HideToTray();
        }
        else
        {
            args.Cancel = true;
            _ = _quitCallback();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsInitialized)
            or nameof(MainViewModel.State)
            or nameof(MainViewModel.ShowSetupWizard)
            or nameof(MainViewModel.DisplayTier)
            or nameof(MainViewModel.IsTierSelectorEnabled)
            or nameof(MainViewModel.HasDraftError)
            or nameof(MainViewModel.HasError)
            or nameof(MainViewModel.HasMatches)
            or nameof(MainViewModel.MatchesCount)
            or nameof(MainViewModel.ShowTraySpeedupHint)
            or nameof(MainViewModel.SelectedLanguageCode)
            or "")
        {
            UpdateVisualStates();
        }
    }

    private void UpdateVisualStates()
    {
        StatusDot.Fill = ViewModel.State switch
        {
            CoordinatorState.Running => new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)),
            CoordinatorState.Calibrating => new SolidColorBrush(Color.FromArgb(255, 245, 158, 11)),
            CoordinatorState.Faulted => new SolidColorBrush(Color.FromArgb(255, 239, 68, 68)),
            _ => new SolidColorBrush(Color.FromArgb(255, 128, 128, 128))
        };

        SetupWizardPanel.Visibility = ViewModel.ShowSetupWizard ? Visibility.Visible : Visibility.Collapsed;
        DashboardPanel.Visibility = ViewModel.ShowDashboard ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = ViewModel.ShowDashboard ? Visibility.Visible : Visibility.Collapsed;
        TraySpeedupHintTextBlock.Visibility = ViewModel.ShowTraySpeedupHint ? Visibility.Visible : Visibility.Collapsed;

        SetupValidationErrorText.Visibility = ViewModel.HasDraftError ? Visibility.Visible : Visibility.Collapsed;
        ApplyTextBoxValidationVisual(SetupInputFrame, SetupTargetInputBox, ViewModel.HasDraftError);

        SyncSetupLanguageCombo();

        ErrorBanner.Visibility = ViewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
        NoMatchesEmptyPanel.Visibility = ViewModel.HasMatches ? Visibility.Collapsed : Visibility.Visible;
        MatchesContentGrid.Visibility = ViewModel.HasMatches ? Visibility.Visible : Visibility.Collapsed;

        UpdateTierButtonStyles();
        AdjustWindowHeightForMatchesAndMode();
    }

    private void UpdateTierButtonStyles()
    {
        Style accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        Style ghost = (Style)Application.Current.Resources["TierGhostButtonStyle"];
        PowerTier active = ViewModel.DisplayTier;

        ApplyTierButtonStyle(TierEcoButton, active == PowerTier.Eco, accent, ghost);
        ApplyTierButtonStyle(TierBalancedButton, active == PowerTier.Balanced, accent, ghost);
        ApplyTierButtonStyle(TierEfficiencyButton, active == PowerTier.Efficiency, accent, ghost);
        ApplyTierButtonStyle(TierMaxButton, active == PowerTier.Max, accent, ghost);
    }

    private static void ApplyTierButtonStyle(Button button, bool isActive, Style accent, Style ghost)
    {
        button.Style = isActive ? accent : ghost;
        button.Padding = new Thickness(4, 0, 4, 0);
        button.CornerRadius = new CornerRadius(4);
        button.FontSize = 12.5;
        button.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void OnSetupContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ViewModel.IsInitialized || !ViewModel.ShowSetupWizard || e.NewSize.Height <= 0)
        {
            return;
        }

        ResizeSetupWindowToContent(e.NewSize.Height);
    }

    private void OnErrorBannerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel.IsInitialized && ViewModel.ShowSetupWizard && SetupContentPanel.ActualHeight > 0)
        {
            ResizeSetupWindowToContent(SetupContentPanel.ActualHeight);
        }
    }

    private void ResizeSetupWindowToContent(double setupContentHeight)
    {
        double errorExtra = ErrorBanner.Visibility == Visibility.Visible && ErrorBanner.ActualHeight > 0
            ? ErrorBanner.ActualHeight + 12.0
            : 0.0;

        // Title bar (38) + Main Viewport top padding (6) + Setup card height + bottom padding (16)
        // keeps left, right, and bottom outer margins equal (16 DIP) matching the card's internal padding.
        double desiredClientHeight = 38.0 + 6.0 + 16.0 + errorExtra + setupContentHeight;
        if (_lastSizedSetupMode == true && Math.Abs(desiredClientHeight - _lastSetupDesiredClientHeight) <= 1.0)
        {
            return;
        }

        bool initialCenter = _lastSizedSetupMode is null;
        _lastSizedSetupMode = true;
        _lastSizedVisibleMatchRows = 0;
        _lastSetupDesiredClientHeight = desiredClientHeight;

        WindowMinSizeHelper.ResizeClientHeightClampedToWorkArea(
            AppWindow,
            _hwnd,
            desiredClientHeight,
            LogicalMinHeight,
            centerOnMonitor: initialCenter);
    }

    private void AdjustWindowHeightForMatchesAndMode()
    {
        if (!ViewModel.IsInitialized)
        {
            return;
        }

        bool isSetup = ViewModel.ShowSetupWizard;
        if (isSetup)
        {
            if (SetupContentPanel.ActualHeight > 0)
            {
                ResizeSetupWindowToContent(SetupContentPanel.ActualHeight);
            }
            return;
        }

        int visibleRows = Math.Clamp(ViewModel.MatchesCount, 0, MaxAutoStretchMatches);
        if (_lastSizedSetupMode == false && _lastSizedVisibleMatchRows == visibleRows)
        {
            return;
        }

        bool initialCenter = _lastSizedSetupMode is null;
        _lastSizedSetupMode = false;
        _lastSizedVisibleMatchRows = visibleRows;

        int targetLogicalHeight = visibleRows == 0
            ? LogicalBaseDashboardHeight
            : LogicalBaseDashboardHeight + 24 + (visibleRows * 38);

        WindowMinSizeHelper.ResizeHeightClampedToWorkArea(
            AppWindow,
            _hwnd,
            targetLogicalHeight,
            LogicalMinHeight,
            centerOnMonitor: initialCenter);
    }

    private void SyncSetupLanguageCombo()
    {
        _syncingLangCombo = true;
        try
        {
            SetupLanguageComboBox.SelectedValue = ViewModel.SelectedLanguageCode;
        }
        finally
        {
            _syncingLangCombo = false;
        }
    }

    private void OnSetupLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLangCombo)
        {
            return;
        }

        if (SetupLanguageComboBox.SelectedValue is string code && !string.IsNullOrWhiteSpace(code))
        {
            ViewModel.SetLanguage(code);
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

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        _openSettingsCallback();
    }

    private async void OnSetupFinishClick(object sender, RoutedEventArgs e)
    {
        if (_isStartingSetup)
        {
            return;
        }

        _isStartingSetup = true;
        try
        {
            await ViewModel.CompleteSetupAndStartAsync();
            UpdateVisualStates();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
        finally
        {
            _isStartingSetup = false;
        }
    }

    private void OnTierButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            return;
        }

        if (Enum.TryParse<PowerTier>(tag, ignoreCase: false, out PowerTier tier))
        {
            ViewModel.SelectPowerTier(tier);
            UpdateTierButtonStyles();
        }
    }

    private async void OnPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.PrimaryActionAsync();
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
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
            cb.IsChecked = actualState;
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
    }

    private void OnCopyFingerprintClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button btn && btn.Tag is string fingerprint)
            {
                var pkg = new DataPackage();
                pkg.SetText(fingerprint);
                Clipboard.SetContent(pkg);
                ViewModel.FlashNotice(ViewModel.Loc.Copied);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
    }

    private async void OnClearAllMatchesClick(object sender, RoutedEventArgs e)
    {
        if (_isDialogOpen || ViewModel.MatchesCount == 0)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = ViewModel.Loc.ClearAllConfirmTitle,
                Content = new TextBlock
                {
                    Text = ViewModel.Loc.FormatClearAllConfirm(ViewModel.MatchesCount),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = ViewModel.Loc.ClearAll,
                CloseButtonText = ViewModel.Loc.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ViewModel.ClearAllMatches();
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private async void OnDeleteMatchClick(object sender, RoutedEventArgs e)
    {
        if (_isDialogOpen || sender is not Button btn || btn.Tag is not MatchedKeyRecord record)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = ViewModel.Loc.DeleteConfirmTitle,
                Content = new TextBlock
                {
                    Text = FormattingHelpers.FormatKeyIdSpaced(record.KeyId),
                    FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                PrimaryButtonText = ViewModel.Loc.Delete,
                CloseButtonText = ViewModel.Loc.Cancel,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                ViewModel.DeleteMatch(record.Id);
            }
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private async void OnExportMatchClick(object sender, RoutedEventArgs e)
    {
        if (_isDialogOpen || sender is not Button btn || btn.Tag is not MatchedKeyRecord record)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var nameBox = new TextBox { PlaceholderText = ViewModel.Loc.NamePlaceholder };
            var emailBox = new TextBox { PlaceholderText = ViewModel.Loc.EmailPlaceholder };
            var passBox = new PasswordBox { PlaceholderText = ViewModel.Loc.PassphrasePlaceholder };
            var encryptSubkeyCheck = new CheckBox
            {
                Content = ViewModel.Loc.ExportIncludeEncryptSubkey,
                IsChecked = true
            };
            var expirationCombo = new ComboBox
            {
                Header = ViewModel.Loc.ExportExpirationLabel,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            expirationCombo.Items.Add(new ComboBoxItem { Content = ViewModel.Loc.ExportExpirationNever, Tag = 0 });
            expirationCombo.Items.Add(new ComboBoxItem { Content = ViewModel.Loc.ExportExpiration1Year, Tag = 365 });
            expirationCombo.Items.Add(new ComboBoxItem { Content = ViewModel.Loc.ExportExpiration2Years, Tag = 730 });
            expirationCombo.Items.Add(new ComboBoxItem { Content = ViewModel.Loc.ExportExpiration5Years, Tag = 1825 });
            expirationCombo.SelectedIndex = 0;

            var keyIdLabel = new TextBlock
            {
                Text = FormattingHelpers.FormatKeyIdSpaced(record.KeyId),
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
            panel.Children.Add(keyIdLabel);
            panel.Children.Add(nameBox);
            panel.Children.Add(emailBox);
            panel.Children.Add(passBox);
            panel.Children.Add(encryptSubkeyCheck);
            panel.Children.Add(expirationCombo);

            var dialog = new ContentDialog
            {
                Title = ViewModel.Loc.ExportTitle,
                Content = panel,
                PrimaryButtonText = ViewModel.Loc.Save,
                CloseButtonText = ViewModel.Loc.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Content.XamlRoot
            };

            void ValidateInputs() =>
                dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text) || !string.IsNullOrWhiteSpace(emailBox.Text);

            nameBox.TextChanged += (_, _) => ValidateInputs();
            emailBox.TextChanged += (_, _) => ValidateInputs();

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string userName = nameBox.Text;
            string userEmail = emailBox.Text;
            string? passphrase = string.IsNullOrEmpty(passBox.Password) ? null : passBox.Password;
            bool includeEncryptSubkey = encryptSubkeyCheck.IsChecked == true;
            int expirationDays = (expirationCombo.SelectedItem as ComboBoxItem)?.Tag is int d ? d : 0;
            DateTimeOffset? expiresAtUtc = expirationDays > 0 ? DateTimeOffset.UtcNow.AddDays(expirationDays) : null;

            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"{record.KeyId}.secret"
            };
            picker.FileTypeChoices.Add("OpenPGP ASCII Armor", new List<string> { ".asc" });
            InitializeWithWindow.Initialize(picker, _hwnd);

            StorageFile? secretFile = await picker.PickSaveFileAsync();
            if (secretFile is null)
            {
                return;
            }

            OpenPgpExportResult exported = await Task.Run(() =>
                ViewModel.ExportMatchKey(record, userName, userEmail, passphrase, includeEncryptSubkey, expiresAtUtc));

            await FileIO.WriteTextAsync(secretFile, exported.PrivateKeyArmor);

            string pubPath = DerivePublicKeyPath(secretFile.Path);
            await File.WriteAllTextAsync(pubPath, exported.PublicKeyArmor);

            ViewModel.FlashNotice(ViewModel.Loc.ExportSuccess);
        }
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private static string DerivePublicKeyPath(string secretPath)
    {
        if (secretPath.EndsWith(".secret.asc", StringComparison.OrdinalIgnoreCase))
        {
            return secretPath[..^".secret.asc".Length] + ".public.asc";
        }

        if (secretPath.EndsWith(".public.asc", StringComparison.OrdinalIgnoreCase))
        {
            return secretPath[..^".public.asc".Length] + ".pub.asc";
        }

        string dir = Path.GetDirectoryName(secretPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(secretPath);
        return Path.Combine(dir, $"{stem}.public.asc");
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
