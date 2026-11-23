using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private const int LogicalDefaultWidth = 480;
    private const int LogicalDefaultHeight = 480;
    private const int LogicalMinWidth = 420;
    private const int LogicalMinHeight = 380;

    private readonly TrayIconService _trayIcon;
    private readonly Func<Task> _quitCallback;
    private readonly Action _openSettingsCallback;
    private readonly IntPtr _hwnd;
    private bool _allowClose;
    private bool _isDialogOpen;
    private bool _syncingLangCombo;

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
        ConfigureWindowChrome();

        SetupLanguageComboBox.ItemsSource = ViewModel.LanguageOptions;
        SetupLanguageComboBox.Loaded += (_, _) => SyncSetupLanguageCombo();

        RateSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSliderReleased), handledEventsToo: true);
        RateSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnSliderReleased), handledEventsToo: true);
        RateSlider.PreviewKeyDown += OnSliderPreviewKeyDown;
        RateSlider.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnSliderKeyUp), handledEventsToo: true);

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

        double scale = GetDpiScale();
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(LogicalDefaultWidth * scale),
            (int)Math.Round(LogicalDefaultHeight * scale)));
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnAppWindowClosing;
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
        if (e.PropertyName is nameof(MainViewModel.State)
            or nameof(MainViewModel.ShowSetupWizard)
            or nameof(MainViewModel.HasDraftError)
            or nameof(MainViewModel.HasError)
            or nameof(MainViewModel.HasMatches)
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

        SetupValidationErrorText.Visibility = ViewModel.HasDraftError ? Visibility.Visible : Visibility.Collapsed;
        ApplyTextBoxValidationVisual(SetupInputFrame, SetupTargetInputBox, ViewModel.HasDraftError);

        SyncSetupLanguageCombo();

        ErrorBanner.Visibility = ViewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
        MatchesCard.Visibility = ViewModel.HasMatches ? Visibility.Visible : Visibility.Collapsed;
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

    private void OnSetupFinishClick(object sender, RoutedEventArgs e)
    {
        ViewModel.CompleteSetup();
        UpdateVisualStates();
    }

    private void OnSliderReleased(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.CommitSliderRelease();
    }

    private void OnSliderPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsSliderEnabled)
        {
            return;
        }

        if (e.Key is VirtualKey.Right or VirtualKey.Up or VirtualKey.PageUp)
        {
            if (ViewModel.SliderValue >= RateControl.UiSliderCappedMax && ViewModel.SliderValue < RateControl.UiSliderFullSpeed)
            {
                ViewModel.SliderValue = RateControl.UiSliderFullSpeed;
                ViewModel.CommitSliderRelease();
                e.Handled = true;
            }
        }
        else if (e.Key is VirtualKey.Left or VirtualKey.Down or VirtualKey.PageDown)
        {
            if (ViewModel.SliderValue >= RateControl.UiSliderFullSpeedThreshold)
            {
                ViewModel.SliderValue = RateControl.UiSliderCappedMax;
                ViewModel.CommitSliderRelease();
                e.Handled = true;
            }
        }
    }

    private void OnSliderKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End)
        {
            ViewModel.CommitSliderRelease();
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

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.StopAsync();
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
                ViewModel.ExportMatchKey(record, userName, userEmail, passphrase));

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
