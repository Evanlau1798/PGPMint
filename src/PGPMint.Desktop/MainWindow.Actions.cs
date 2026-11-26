using Avalonia.Controls;
using Avalonia.Interactivity;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

public sealed partial class MainWindow
{
    private async void OnSetupStartClick(object? sender, RoutedEventArgs e)
    {
        await RunAsync(() =>
        {
            if (_session.Coordinator is not { } coordinator) return Task.CompletedTask;
            KeyIdPattern parsed = KeyIdPattern.Parse(SetupTargetBox.Text ?? string.Empty);
            coordinator.SetTargetPattern(parsed);
            _session.StateStore.Update(s =>
            {
                s.TargetInput = parsed.Suffix;
                s.Language = _strings.Language;
                s.HasCompletedSetup = true;
            });
            SyncViewVisibility();
            _platform?.SyncMenuState();
            return Task.CompletedTask;
        });
    }

    private void OnLanguageComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox combo || combo.SelectedItem is not LanguageOption option) return;
        try { ApplyLanguageSelection(option.Code); }
        catch { SetError(L("OperationFailed")); }
    }

    internal void ApplyLanguageSelection(string code)
    {
        if (_session.Coordinator is not null) _session.StateStore.Update(s => s.Language = code);
        _strings.Apply(code);
        ApplyTierTooltips();
        SyncStaticTexts();
        SyncViewVisibility();
        _renderedMatchKey = string.Empty;
        ForceRefresh();
        _platform?.SyncMenuState();
        LanguageChanged?.Invoke();
    }

    internal void OnSettingsChangedFromDialog(bool flashApplied)
    {
        SyncViewVisibility();
        if (flashApplied) FlashNotice(L("Applied"));
        ForceRefresh();
        _platform?.SyncMenuState();
    }

    internal async Task RecalibrateFromSettingsAsync()
    {
        await RunAsync(async () =>
        {
            if (_session.Coordinator is not { } coordinator) return;
            AppPersistedState state = _session.StateStore.Load();
            if (!KeyIdPattern.TryParse(state.TargetInput, out KeyIdPattern? parsed, out _) || parsed is null) return;
            coordinator.SetTargetPattern(parsed);
            if (coordinator.InvalidateCalibrationsForRecalibration()) await coordinator.StartSearchAsync(parsed);
            FlashNotice(L("Recalibrating"));
            _platform?.SyncMenuState();
        });
    }

    internal async Task LockVaultFromSettingsAsync()
    {
        await RunAsync(async () =>
        {
            _settingsWindow?.Close();
            await _session.DisposeAsync();
            if (_platform is not null) await _platform.SetSearchingAsync(false);
            SyncViewVisibility();
            _platform?.SyncMenuState();
        });
    }

    internal Task TriggerPrimaryActionFromTrayAsync() => ExecutePrimaryActionAsync();

    internal void SetTierFromTray(PowerTier tier)
    {
        try
        {
            if (_session.Coordinator is { } coordinator)
            {
                coordinator.SetPowerTier(tier);
                ForceRefresh();
                _platform?.SyncMenuState();
            }
        }
        catch { SetError(L("OperationFailed")); }
    }

    private async void OnUnlockClick(object? sender, RoutedEventArgs e) => await RunAsync(UnlockAsync);
    private async void OnPrimaryActionClick(object? sender, RoutedEventArgs e) => await ExecutePrimaryActionAsync();

    private void OnTierClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse(tag, out PowerTier tier) && _session.Coordinator is { } coordinator)
        {
            try
            {
                SetError(null);
                coordinator.SetPowerTier(tier);
                ForceRefresh();
                _platform?.SyncMenuState();
            }
            catch { SetError(L("OperationFailed")); }
        }
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_session.Coordinator is null) return;
        if (_settingsWindow is { IsVisible: true }) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(this, _session, _strings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }

    private Task ExecutePrimaryActionAsync() => RunAsync(async () =>
    {
        if (_session.Coordinator is not { } coordinator) return;
        if (coordinator.CurrentState is CoordinatorState.Running or CoordinatorState.Calibrating or CoordinatorState.Paused)
        {
            await coordinator.StopAsync();
        }
        else
        {
            AppPersistedState state = _session.StateStore.Load();
            if (string.IsNullOrWhiteSpace(state.TargetInput)) throw new ArgumentException(L("TargetRequired"));
            KeyIdPattern parsed = KeyIdPattern.Parse(state.TargetInput);
            coordinator.SetTargetPattern(parsed);
            await coordinator.StartSearchAsync(parsed);
        }
        _platform?.SyncMenuState();
    });

    private async Task UnlockAsync()
    {
        if (await TryAutomaticUnlockAsync()) return;
        SecretInput? input = await new SecretDialog(_strings).ShowDialog<SecretInput?>(this);
        if (input is null) return;
        try { await Task.Run(() => _session.Unlock(input.Password)); }
        finally { Array.Clear(input.Password); }
        ApplyUnlockedState();
    }

    private async Task<bool> TryAutomaticUnlockAsync()
    {
        if (!_session.SupportsAutomaticUnlock) return false;
        SetError(L("OpeningSystemVault"));
        bool opened = await Task.Run(_session.TryUnlockAutomatically);
        if (opened) ApplyUnlockedState();
        else SetError(null);
        return opened;
    }

    private void ApplyUnlockedState()
    {
        SetError(null);
        AppPersistedState state = _session.StateStore.Load();
        _strings.Apply(state.Language);
        if (_session.Coordinator is { } coordinator)
        {
            if (KeyIdPattern.TryParse(state.TargetInput, out KeyIdPattern? parsed, out _) && parsed is not null)
                coordinator.SetTargetPattern(parsed);
            coordinator.SetAutoPauseOnHit(state.AutoPauseOnHit);
        }
        ApplyTierTooltips();
        SyncStaticTexts();
        SyncViewVisibility();
        _renderedMatchKey = string.Empty;
        _platform?.SyncMenuState();
        LanguageChanged?.Invoke();
    }
}
