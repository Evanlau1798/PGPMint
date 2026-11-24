using System.Security.Cryptography;
using PGPMint.Core.Crypto;
using PGPMint.Core.Search;

namespace PGPMint.App.ViewModels;

public sealed partial class MainViewModel
{
    public OpenPgpExportResult ExportMatchKey(MatchedKeyRecord record, string userName, string? userEmail, string? passphrase)
    {
        byte[] seed = _vaultStore.DecryptPrivateSeed(record);
        byte[] publicKey = Convert.FromHexString(record.PublicKeyHex);
        try
        {
            return OpenPgpArmorExporter.ExportKeyPair(new OpenPgpExportRequest(
                PrivateSeed: seed,
                PublicKey: publicKey,
                CreationTimestamp: record.CreationTimestamp,
                UserName: userName,
                UserEmail: userEmail,
                Passphrase: passphrase));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public void DeleteMatch(string recordId)
    {
        try
        {
            if (_coordinator.DeleteVaultMatch(recordId))
            {
                ReloadMatchesFromVault();
                ApplyTelemetry(_coordinator.LatestTelemetry);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    public void ClearAllMatches()
    {
        try
        {
            if (_coordinator.ClearVaultMatches() > 0)
            {
                ReloadMatchesFromVault();
                ApplyTelemetry(_coordinator.LatestTelemetry);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    public void FlashNotice(string text)
    {
        TransientNotice = text;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2600);
            _dispatcher.TryEnqueue(() =>
            {
                if (TransientNotice == text)
                {
                    TransientNotice = string.Empty;
                }
            });
        });
    }

    public async Task PrepareForQuitAsync()
    {
        bool wasRunning = _coordinator.CurrentState is CoordinatorState.Running or CoordinatorState.Calibrating;
        _stateStore.Update(s => s.WasRunningOnExit = wasRunning);
        _powerKeepAlive.SetActive(false);
        await StopAsync();
    }

    private void OnCoordinatorTelemetryUpdated(object? sender, CoordinatorTelemetry telemetry)
    {
        _dispatcher.TryEnqueue(() => ApplyTelemetry(_coordinator.LatestTelemetry));
    }

    private void OnCoordinatorMatchFound(object? sender, MatchedKeyRecord record)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ReloadMatchesFromVault();
            ApplyTelemetry(_coordinator.LatestTelemetry);

            long nowTick = Environment.TickCount64;
            if (AutoPauseOnHit || nowTick - _lastHitNotifyTick >= NotificationThrottleMs)
            {
                _lastHitNotifyTick = nowTick;
                string formattedId = FormattingHelpers.FormatKeyIdSpaced(record.KeyId);
                _trayIcon.ShowNotification(Loc.HitNotificationTitle, formattedId);
            }
        });
    }

    public bool HasMatchForActivePattern()
    {
        if (_activePatternObj is null || Matches.Count == 0)
        {
            return false;
        }

        foreach (MatchedKeyItemViewModel item in Matches)
        {
            if (string.Equals(item.Record.MatchedPattern, _activePatternObj.CanonicalMask, StringComparison.OrdinalIgnoreCase) ||
                _activePatternObj.MatchesKeyIdHex(item.Record.KeyId))
            {
                return true;
            }
        }

        return false;
    }

    private void ReloadMatchesFromVault()
    {
        try
        {
            IReadOnlyList<MatchedKeyRecord> records = _vaultStore.LoadMatches();
            Matches.Clear();
            foreach (MatchedKeyRecord rec in records)
            {
                Matches.Add(new MatchedKeyItemViewModel(rec, Loc));
            }

            MatchesCount = Matches.Count;
            HasMatches = MatchesCount > 0;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }
}
