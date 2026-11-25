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
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
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
            session.StateStore.Update(s => s.Language = "en-US");
            var strings = new LocalizationService();
            strings.Apply("en-US");
            var window = new MainWindow(session, strings);
            window.Show();
            var controls = window.GetLogicalDescendants().ToArray();
            var language = controls.OfType<ComboBox>().Single(c => c.Items.Contains("zh-Hant"));
            var tier = controls.OfType<ComboBox>().Single(c => c.ItemCount == 4);
            using (var blocked = new FileStream(session.StateStore.StateFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Null(Record.Exception(() => language.SelectedItem = "zh-Hant"));
                Assert.Null(Record.Exception(() => tier.SelectedIndex = (tier.SelectedIndex + 1) % 4));
            }
            Assert.Contains(controls.OfType<TextBlock>(), t => t.Text == strings["OperationFailed"]);
            await window.QuitAsync();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task UnlockedWindowDiagnosesNoGpuAndShowsFourPowerTiers()
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
            var controls = window.GetLogicalDescendants().ToArray();
            Assert.Contains(controls.OfType<TextBlock>(), t => t.Text == "No hardware GPU detected");
            Assert.Contains(controls.OfType<ComboBox>(), c => c.ItemCount == 4);
            Assert.Contains(controls.OfType<TextBlock>(), t => t.Text?.Contains("CPU-verified hits") == true);
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
        strings.Apply("en-US");
        Assert.Equal("Start", strings["Start"]);
        Assert.NotEqual("VaultPassword", strings["VaultPassword"]);
        strings.Apply("zh-Hant");
        Assert.Equal("開始", strings["Start"]);
        Assert.Equal("金鑰庫密碼（必填）", strings["VaultPassword"]);
        strings.Apply("unsupported");
        Assert.Equal("auto", strings.Language);
    }

    [AvaloniaFact]
    public async Task TrayHasAMenuBeforeAttachingAndKeepsItsNativeMenuIdentity()
    {
        await using var session = new DesktopSession(new EmptyBackend(), Path.Combine(Path.GetTempPath(), "unused-tray"));
        var window = new MainWindow(session, new LocalizationService());
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
            await using var platform = new PlatformServices(window, session, new LocalizationService());
            Assert.True(attachedWithMenu);
            var icon = TrayIcon.GetIcons(app)!.Single();
            var menu = icon.Menu;
            typeof(PlatformServices).GetMethod("UpdateMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(platform, null);
            Assert.Same(menu, icon.Menu);
            Assert.Equal(6, icon.Menu!.Items.Count);
        }
        finally { app.PropertyChanged -= Attached; await window.QuitAsync(); }
    }

    private sealed class EmptyBackend : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => [];
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => throw new NotSupportedException();
    }
}
