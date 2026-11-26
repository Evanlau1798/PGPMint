using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Desktop;

public sealed partial class MainWindow
{
    private void RenderMatches(IReadOnlyList<MatchedKeyRecord> matches)
    {
        NoMatchesPanel.IsVisible = matches.Count == 0;
        MatchesContentPanel.IsVisible = matches.Count > 0;
        if (matches.Count == 0)
        {
            if (_renderedMatchKey.Length == 0) return;
            _renderedMatchKey = string.Empty;
            MatchesListPanel.Children.Clear();
            RefreshAutoHeight();
            return;
        }

        string key = $"{_strings.ActiveCode}|{string.Join(',', matches.Select(m => m.Id))}";
        if (key == _renderedMatchKey) return;
        _renderedMatchKey = key;
        MatchesCountText.Text = $"{L("Saved")}: {matches.Count}";
        MatchesListPanel.Children.Clear();
        foreach (MatchedKeyRecord record in matches) MatchesListPanel.Children.Add(BuildMatchRow(record));
        RefreshAutoHeight();
    }

    private void RefreshAutoHeight()
    {
        if (SizeToContent != SizeToContent.Height) return;
        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.Height;
    }

    private Control BuildMatchRow(MatchedKeyRecord record)
    {
        var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto"), ColumnSpacing = 4 };
        var idText = new TextBlock
        {
            Text = FormattingHelpers.FormatKeyIdSpaced(record.KeyId),
            FontFamily = this.FindResource("MonoFont") as FontFamily ?? FontFamily.Default,
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(idText, 0);

        var copyBtn = new Button { Classes = { "icon-ghost" }, Content = new PathIcon { Data = Icons.Copy, Width = 12, Height = 12 } };
        ToolTip.SetTip(copyBtn, L("Copy"));
        copyBtn.Click += async (_, _) =>
        {
            if (GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(record.Fingerprint);
                FlashNotice(L("Copied"));
            }
        };
        Grid.SetColumn(copyBtn, 1);

        var exportBtn = new Button
        {
            Content = ".asc",
            FontFamily = this.FindResource("MonoFont") as FontFamily ?? FontFamily.Default,
            FontSize = 11.5,
            Padding = new Thickness(7, 3)
        };
        exportBtn.Click += async (_, _) => await RunAsync(() => ExportRecordAsync(record));
        Grid.SetColumn(exportBtn, 2);

        var deleteBtn = new Button { Classes = { "icon-ghost" }, Content = new PathIcon { Data = Icons.Delete, Width = 12, Height = 12 } };
        ToolTip.SetTip(deleteBtn, L("DeleteKeyTooltip"));
        deleteBtn.Click += async (_, _) =>
        {
            if (_session.Coordinator is { } c && await ConfirmAsync(this, L("DeleteConfirmTitle"), record.Fingerprint, _strings))
                await RunAsync(() => { c.DeleteVaultMatch(record.Id); return Task.CompletedTask; });
        };
        Grid.SetColumn(deleteBtn, 3);

        row.Children.Add(idText);
        row.Children.Add(copyBtn);
        row.Children.Add(exportBtn);
        row.Children.Add(deleteBtn);
        return row;
    }

    private async void OnClearAllClick(object? sender, RoutedEventArgs e)
    {
        if (_session.Coordinator is not { } coordinator) return;
        IReadOnlyList<MatchedKeyRecord> matches = _session.Matches();
        if (matches.Count == 0) return;
        string msg = DesktopDisplayMapper.Format(L("ClearAllConfirmFormat"), matches.Count.ToString());
        if (await ConfirmAsync(this, L("ClearAllConfirmTitle"), msg, _strings))
            await RunAsync(() => { coordinator.ClearVaultMatches(); return Task.CompletedTask; });
    }

    private async Task ExportRecordAsync(MatchedKeyRecord record)
    {
        SecretInput? input = await new SecretDialog(_strings, export: true).ShowDialog<SecretInput?>(this);
        if (input is null) return;
        try
        {
            string? secret = await SavePathAsync("ExportPrivate", record.KeyId + ".secret.asc");
            if (secret is null) return;
            string? pub = await SavePathAsync("ExportPublic", record.KeyId + ".public.asc");
            if (pub is null) return;
            await Task.Run(() => _session.Export(record, input.Uid, input.Email, new string(input.Password), secret, pub));
            FlashNotice(L("ExportSuccess"));
        }
        finally { Array.Clear(input.Password); }
    }

    private async Task<string?> SavePathAsync(string title, string suggested)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L(title),
            SuggestedFileName = suggested,
            DefaultExtension = "asc",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("OpenPGP ASCII armor") { Patterns = ["*.asc"] }]
        });
        return file is null ? null : file.TryGetLocalPath() ?? throw new IOException(L("LocalFileRequired"));
    }

    internal static async Task<bool> ConfirmAsync(Window owner, string title, string message, LocalizationService strings)
    {
        var dialog = new Window
        {
            Title = title,
            Icon = AppIcon.Load(),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        dialog.Bind(BackgroundProperty, dialog.GetResourceObservable("AppWindowBg"));
        var yes = new Button { Content = strings["Apply"], Classes = { "accent" }, Height = 32, MinWidth = 76 };
        var no = new Button { Content = strings["Cancel"], IsCancel = true, Height = 32, MinWidth = 76 };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } }
            }
        };
        return await dialog.ShowDialog<bool>(owner);
    }
}
