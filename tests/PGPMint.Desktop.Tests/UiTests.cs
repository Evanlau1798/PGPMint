using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using PGPMint.Core.Search;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(PGPMint.Desktop.Tests.UiBootstrap))]

namespace PGPMint.Desktop.Tests;

public static class UiBootstrap
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class UiTests
{
    [AvaloniaFact]
    public async Task SettingsWriteFailureIsDisplayedWithoutEscapingUiEvents()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "tmp",
            "pgpmint-desktop-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = new DesktopSession(new EmptyBackend(), directory);
            session.Unlock("test password");
            session.StateStore.Update(s =>
            {
                s.Language = "en-US";
                s.TargetInput = "AB";
                s.HasCompletedSetup = true;
            });
            var strings = new LocalizationService();
            strings.Apply("en-US");
            var window = new MainWindow(session, strings);
            window.Show();
            var settings = new SettingsWindow(window, session, strings);
            settings.Show(window);
            var settingsControls = settings.GetLogicalDescendants().ToArray();
            var language = settingsControls.OfType<ComboBox>().Single(c => c.Items.OfType<LanguageOption>().Any(o => o.Code == "zh-Hant"));
            var zhHantOption = language.Items.OfType<LanguageOption>().Single(o => o.Code == "zh-Hant");
            var autoPause = settingsControls.OfType<CheckBox>().First();
            using (var blocked = new FileStream(session.StateStore.StateFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Null(Record.Exception(() => language.SelectedItem = zhHantOption));
                Assert.Null(Record.Exception(() => autoPause.IsChecked = !autoPause.IsChecked));
            }
            Assert.Contains(settingsControls.OfType<TextBlock>(), t => t.Text == strings["OperationFailed"]);
            await window.QuitAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task SetupWizardDiagnosesNoGpuAndCompletesIntoDashboard()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "tmp",
            "pgpmint-desktop-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = new DesktopSession(new EmptyBackend(), directory);
            session.Unlock("test password");
            var strings = new LocalizationService();
            strings.Apply("en-US");
            var window = new MainWindow(session, strings);
            window.Show();
            window.Refresh();
            var setupPanel = window.FindControl<Border>("SetupWizardPanel")!;
            var dashboardPanel = window.FindControl<StackPanel>("DashboardPanel")!;
            var setupBox = window.FindControl<TextBox>("SetupTargetBox")!;
            var finishBtn = window.FindControl<Button>("SetupStartButton")!;
            Assert.True(setupPanel.IsVisible);
            Assert.False(dashboardPanel.IsVisible);
            Assert.False(finishBtn.IsEnabled);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "No hardware GPU detected");

            setupBox.Text = "CAFE";
            Assert.True(finishBtn.IsEnabled);
            finishBtn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);

            Assert.False(setupPanel.IsVisible);
            Assert.True(dashboardPanel.IsVisible);
            Assert.Equal("CAFE", session.StateStore.Load().TargetInput);
            Assert.True(session.StateStore.Load().HasCompletedSetup);
            await window.QuitAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task DashboardShowsFourPowerTiersWithTooltipsAndHonestBadge()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "tmp",
            "pgpmint-desktop-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = new DesktopSession(new EmptyBackend(), directory);
            session.Unlock("test password");
            session.StateStore.Update(s =>
            {
                s.TargetInput = "DEAD";
                s.HasCompletedSetup = true;
            });
            var strings = new LocalizationService();
            strings.Apply("en-US");
            var window = new MainWindow(session, strings);
            window.Show();
            window.Refresh();

            string[] tierNames = ["TierEcoButton", "TierBalancedButton", "TierEfficiencyButton", "TierMaxButton"];
            string[] tooltipKeys = ["TierEcoTooltip", "TierBalancedTooltip", "TierEfficiencyTooltip", "TierMaxTooltip"];
            for (int i = 0; i < tierNames.Length; i++)
            {
                var btn = window.FindControl<Button>(tierNames[i])!;
                Assert.True(ToolTip.GetShowOnDisabled(btn));
                Assert.Equal(strings[tooltipKeys[i]], ToolTip.GetTip(btn));
            }
            var badge = window.FindControl<TextBlock>("EfficiencyBadgeText")!;
            Assert.NotEqual("0 W", badge.Text);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "No hardware GPU detected");
            await window.QuitAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public void PasswordIsMaskedAndClearedWhenDialogCloses()
    {
        var dialog = new SecretDialog(new LocalizationService());
        dialog.Show();
        dialog.PasswordInput.Text = "never retain me";
        Assert.NotEqual(default, dialog.PasswordInput.PasswordChar);
        dialog.Close();
        Assert.True(string.IsNullOrEmpty(dialog.PasswordInput.Text));
    }

    [AvaloniaFact]
    public void TakingInputTransfersAndClearsPasswordControl()
    {
        var dialog = new SecretDialog(new LocalizationService(), export: true);
        dialog.PasswordInput.Text = "export password";
        dialog.UidInput.Text = "Test User";
        var input = dialog.TakeInput();
        Assert.Equal("export password".ToCharArray(), input.Password);
        Assert.Equal("Test User", input.Uid);
        Assert.True(string.IsNullOrEmpty(dialog.PasswordInput.Text));
        Array.Clear(input.Password);
    }

    [AvaloniaFact]
    public async Task HiddenWindowDoesNotRefreshAndRefreshResumesWhenShown()
    {
        await using var session = new DesktopSession(new EmptyBackend(), Path.Combine(Path.GetTempPath(), "unused-desktop"));
        var window = new MainWindow(session, new LocalizationService());
        Assert.True(window.RefreshInterval >= TimeSpan.FromMilliseconds(250));
        window.Refresh();
        Assert.Equal(0, window.RenderCount);
        window.Show();
        window.Refresh();
        Assert.True(window.RenderCount > 0);
        int first = window.RenderCount;
        for (int i = 0; i < 10; i++) window.Refresh();
        Assert.Equal(first, window.RenderCount);
        window.Hide();
        int before = window.RenderCount;
        window.Refresh();
        Assert.Equal(before, window.RenderCount);
        await window.QuitAsync();
    }

    [Fact]
    public void ExistingAndDesktopDictionariesSwitchLanguages()
    {
        var strings = new LocalizationService();
        Assert.Equal(
            ["auto", "en-US", "zh-Hant", "zh-Hans", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "pt-BR", "ru-RU"],
            strings.LanguageOptions.Select(o => o.Code).ToArray());
        Assert.Equal("Portugu\u00EAs (Brasil)", strings.LanguageOptions.Single(o => o.Code == "pt-BR").DisplayName);

        strings.Apply("en-US");
        Assert.Equal("Start", strings["Start"]);
        Assert.NotEqual("VaultPassword", strings["VaultPassword"]);
        Assert.Equal("~42.3s", FormattingHelpers.FormatEta(TimeSpan.FromSeconds(42.3), strings.EtaFormats));

        strings.Apply("zh-Hant");
        Assert.Equal("\u958B\u59CB", strings["Start"]);
        Assert.Equal("\u91D1\u9470\u5EAB\u5BC6\u78BC\uFF08\u5FC5\u586B\uFF09", strings["VaultPassword"]);

        strings.Apply("zh-Hans");
        Assert.Equal("\u5F00\u59CB", strings["Start"]);
        Assert.Equal("\u5BC6\u94A5\u5E93\u5BC6\u7801\uFF08\u5FC5\u586B\uFF09", strings["VaultPassword"]);
        Assert.Equal("~42.3 \u79D2", FormattingHelpers.FormatEta(TimeSpan.FromSeconds(42.3), strings.EtaFormats));

        strings.Apply("ja-JP");
        Assert.Equal("\u958B\u59CB", strings["Start"]);
        Assert.NotEqual("VaultPassword", strings["VaultPassword"]);
        Assert.Equal("~42.3\u79D2", FormattingHelpers.FormatEta(TimeSpan.FromSeconds(42.3), strings.EtaFormats));

        foreach (string code in new[] { "ko-KR", "de-DE", "fr-FR", "es-ES", "pt-BR", "ru-RU" })
        {
            strings.Apply(code);
            Assert.Equal(code, strings.ActiveCode);
            Assert.NotEqual("Start", strings["Start"] == code ? code : "");
            Assert.NotEqual("VaultPassword", strings["VaultPassword"]);
        }

        strings.Apply("unsupported");
        Assert.Equal("auto", strings.Language);
    }

    [AvaloniaFact]
    public async Task TrayHasAMenuBeforeAttachingAndKeepsItsNativeMenuIdentity()
    {
        await using var session = new DesktopSession(new EmptyBackend(), Path.Combine(Path.GetTempPath(), "unused-tray"));
        var strings = new LocalizationService();
        strings.Apply("en-US");
        var window = new MainWindow(session, strings);
        bool attachedWithMenu = false;
        var app = Avalonia.Application.Current!;
        void Attached(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property == TrayIcon.IconsProperty)
                attachedWithMenu = TrayIcon.GetIcons(app)?.Single().Menu is not null;
        }
        app.PropertyChanged += Attached;
        try
        {
            await using var platform = new PlatformServices(window, session, strings);
            Assert.True(attachedWithMenu);
            var icon = TrayIcon.GetIcons(app)!.Single();
            var menu = icon.Menu;
            typeof(PlatformServices).GetMethod("UpdateMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(platform, null);
            Assert.Same(menu, icon.Menu);
            Assert.Equal(6, icon.Menu!.Items.Count);
            var labels = icon.Menu.Items.OfType<NativeMenuItem>().Select(i => i.Header).ToArray();
            Assert.DoesNotContain(strings["Hide"], labels);
            Assert.Contains(strings["Open"], labels);
            Assert.Contains(strings["PowerTier"], labels);
            Assert.Contains(strings["Quit"], labels);
            var tierSubmenu = icon.Menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == strings["PowerTier"]).Menu;
            Assert.NotNull(tierSubmenu);
            Assert.Equal(4, tierSubmenu!.Items.Count);
        }
        finally { app.PropertyChanged -= Attached; await window.QuitAsync(); }
    }

    [AvaloniaFact]
    public async Task SystemVaultStartupDoesNotShowAHumanPasswordDialog()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-auto-ui-" + Guid.NewGuid().ToString("N"));
        await using var session = new DesktopSession(new EmptyBackend(), directory, new SessionTests.TestSystemStore());
        var window = new MainWindow(session, new LocalizationService());
        try
        {
            window.Show();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (session.Coordinator is null && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Assert.NotNull(session.Coordinator);
            Assert.Empty(window.OwnedWindows);
            Assert.True(PGPMint.Core.Storage.AutomaticVault.IsAutomatic(directory));
        }
        finally { await window.QuitAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task SystemVaultFailureLeavesTheWindowUsableAndDoesNotRequestAHumanPassword()
    {
        string directory = Path.Combine(Path.GetTempPath(), "PGPMint-failed-auto-ui-" + Guid.NewGuid().ToString("N"));
        var store = new FailedSystemStore();
        await using var session = new DesktopSession(new EmptyBackend(), directory, store);
        var strings = new LocalizationService();
        var window = new MainWindow(session, strings);
        try
        {
            window.Show();
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (!window.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == strings["SystemVaultFailed"])
                && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == strings["SystemVaultFailed"]);
            Assert.Null(session.Coordinator);
            Assert.Empty(window.OwnedWindows);
            Assert.True(window.IsVisible);
            using var released = new PGPMint.Core.Storage.VaultSession(directory);
            Assert.False(PGPMint.Core.Storage.AutomaticVault.IsAutomatic(directory));
        }
        finally { await window.QuitAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void EmptyResultsHintShowsHighIdlePowerWarningOnNonMaxAndRestoresTrayHintOnMax()
    {
        var strings = new LocalizationService();
        strings.Apply("zh-Hant");

        static CoordinatorTelemetry MakeTelemetry(CoordinatorState state, PowerTier tier, bool highIdle) =>
            new(
                State: state,
                CalibrationRemainingSeconds: 0.0,
                TotalActualHps: 10e9,
                TotalTargetHps: 10_000_000_000,
                IsFullSpeed: tier == PowerTier.Max,
                TotalBaselineHps: 78e9,
                AllEnabledFullyCalibrated: true,
                SessionChecked: 1000,
                TotalChecked: 1000,
                TotalMatchesCount: 0,
                CumulativeProbability: 0.0,
                EstimatedMeanWait: TimeSpan.FromMinutes(1),
                ErrorMessage: null,
                Devices: [])
            {
                ActiveTier = tier,
                HasHighIdlePowerFloor = highIdle
            };

        Assert.Equal((true, strings["HighIdlePowerHint"]),
            DesktopDisplayMapper.ComputeEmptyResultsHint(MakeTelemetry(CoordinatorState.Running, PowerTier.Balanced, true), hasMatches: false, minimizeToTrayOnClose: false, strings));
        Assert.Equal((true, strings["HighIdlePowerHint"]),
            DesktopDisplayMapper.ComputeEmptyResultsHint(MakeTelemetry(CoordinatorState.Running, PowerTier.Eco, true), hasMatches: false, minimizeToTrayOnClose: true, strings));
        Assert.Equal((true, strings["TraySpeedupGenericHint"]),
            DesktopDisplayMapper.ComputeEmptyResultsHint(MakeTelemetry(CoordinatorState.Running, PowerTier.Max, true), hasMatches: false, minimizeToTrayOnClose: true, strings));
        Assert.Equal((false, string.Empty),
            DesktopDisplayMapper.ComputeEmptyResultsHint(MakeTelemetry(CoordinatorState.Running, PowerTier.Max, true), hasMatches: false, minimizeToTrayOnClose: false, strings));
        Assert.Equal((true, strings["TraySpeedupGenericHint"]),
            DesktopDisplayMapper.ComputeEmptyResultsHint(MakeTelemetry(CoordinatorState.Running, PowerTier.Balanced, false), hasMatches: false, minimizeToTrayOnClose: true, strings));
        Assert.Equal((false, string.Empty),
            DesktopDisplayMapper.ComputeEmptyResultsHint(MakeTelemetry(CoordinatorState.Running, PowerTier.Balanced, true), hasMatches: true, minimizeToTrayOnClose: true, strings));
    }

    [Fact]
    public void DisplayMapperIncludesTemperatureThermalStatusAndMultiGpuTooltip()
    {
        var strings = new LocalizationService();
        strings.Apply("en-US");

        string dir = Path.Combine(Path.GetTempPath(), "pgpmint-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var pattern = KeyIdPattern.Parse("BEEF");
            var dev0 = new GpuDeviceInfo("gpu0", 0, "RTX 3090 #0", 0x10DE, 0x2204, 0, 0xA1, 24UL << 30, "595.99", true, "OpenCL");
            var dev1 = new GpuDeviceInfo("gpu1", 0, "RTX 3090 #1", 0x10DE, 0x2204, 0, 0xA1, 24UL << 30, "595.99", true, "OpenCL");
            using var protector = PGPMint.Core.Storage.PasswordSeedProtector.Open(dir, "test-pw");
            using var coordinator = new MultiGpuCoordinator(
                new EmptyBackend(),
                new PGPMint.Core.Storage.AppStateStore(dir),
                new PGPMint.Core.Storage.VaultStore(dir, protector));

            var t = new CoordinatorTelemetry(
                State: CoordinatorState.Running,
                CalibrationRemainingSeconds: 0.0,
                TotalActualHps: 46.4e9,
                TotalTargetHps: 50_000_000_000,
                IsFullSpeed: true,
                TotalBaselineHps: 50e9,
                AllEnabledFullyCalibrated: true,
                SessionChecked: 1000,
                TotalChecked: 1000,
                TotalMatchesCount: 0,
                CumulativeProbability: 0.01,
                EstimatedMeanWait: TimeSpan.FromSeconds(5),
                ErrorMessage: null,
                Devices:
                [
                    new GpuTelemetrySnapshot(dev0.DeviceKey, dev0.Name, "OpenCL", true, true, 21.2e9, 25_000_000_000, 25e9, true, null)
                    {
                        PowerWatts = 242, HasPowerSensor = true, TemperatureCelsius = 83, HasTemperatureSensor = true,
                        IsThermalThrottled = true, IsThermalGuardActive = true
                    },
                    new GpuTelemetrySnapshot(dev1.DeviceKey, dev1.Name, "OpenCL", true, true, 25.2e9, 25_000_000_000, 25e9, true, null)
                    {
                        PowerWatts = 299, HasPowerSensor = true, TemperatureCelsius = 66, HasTemperatureSensor = true
                    }
                ])
            {
                ActiveTier = PowerTier.Max,
                TotalPowerWatts = 541,
                HasPowerSensor = true,
                HasTemperatureSensor = true,
                MaxTemperatureCelsius = 83,
                IsThermalThrottled = true,
                IsThermalGuardActive = true,
                MaxGpuTemperatureLimitCelsius = 80
            };

            TelemetryDisplay display = DesktopDisplayMapper.BuildDisplay(coordinator, t, pattern, [], strings, busy: false);
            Assert.Contains("83\u00B0C", display.BadgeText);
            Assert.NotNull(display.BadgeTooltip);
            Assert.Contains(strings["ThermalGuardActiveSuffix"], display.BadgeTooltip);
            Assert.Contains(strings["ThermalThrottleWarningSuffix"], display.BadgeTooltip);
            Assert.Contains("RTX 3090 #0", display.BadgeTooltip);
            Assert.Contains("RTX 3090 #1", display.BadgeTooltip);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FailedSystemStore : PGPMint.Core.Storage.IVaultSecretStore
    {
        public string Kind => "test-failure";
        public char[]? Read(string account) => throw new IOException("keychain locked");
        public void Create(string account, ReadOnlySpan<char> secret) => throw new IOException("keychain locked");
        public void Delete(string account) => throw new IOException("keychain locked");
    }

    private sealed class EmptyBackend : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => throw new NotSupportedException();
    }
}
