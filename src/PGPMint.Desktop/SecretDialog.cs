using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PGPMint.Desktop;

public sealed class SecretDialog : Window
{
    private sealed record ExpirationChoice(int Days, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    internal TextBox PasswordInput { get; } = new() { PasswordChar = '●' };
    internal TextBox UidInput { get; } = new();
    internal TextBox EmailInput { get; } = new();
    internal CheckBox IncludeEncryptSubkeyCheck { get; } = new() { IsChecked = true };
    internal ComboBox ExpirationCombo { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    public SecretDialog(LocalizationService strings, bool export = false)
    {
        Title = strings[export ? "ExportTitle" : "Unlock"];
        Icon = AppIcon.Load();
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("AppWindowBg"));

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        void Input(string label, TextBox input)
        {
            body.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
            AutomationProperties.SetName(input, label);
            body.Children.Add(input);
        }

        if (export)
        {
            Input(strings["NamePlaceholder"], UidInput);
            Input(strings["EmailPlaceholder"], EmailInput);
        }
        else
        {
            var hint = new TextBlock { Text = strings["VaultHint"], FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));
            body.Children.Add(hint);
        }

        Input(strings[export ? "ExportPassword" : "VaultPassword"], PasswordInput);
        if (export)
        {
            IncludeEncryptSubkeyCheck.Content = strings["ExportIncludeEncryptSubkey"];
            body.Children.Add(IncludeEncryptSubkeyCheck);

            body.Children.Add(new TextBlock { Text = strings["ExportExpirationLabel"], FontWeight = FontWeight.SemiBold, FontSize = 12.5 });
            ExpirationChoice[] expOptions =
            [
                new(0, strings["ExportExpirationNever"]),
                new(365, strings["ExportExpiration1Year"]),
                new(730, strings["ExportExpiration2Years"]),
                new(1825, strings["ExportExpiration5Years"])
            ];
            ExpirationCombo.ItemsSource = expOptions;
            ExpirationCombo.SelectedIndex = 0;
            AutomationProperties.SetName(ExpirationCombo, strings["ExportExpirationLabel"]);
            body.Children.Add(ExpirationCombo);
        }
        var error = new TextBlock { FontSize = 12, Foreground = Brush.Parse("#EF4444"), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(error);

        var ok = new Button { Content = strings[export ? "Save" : "Unlock"], Classes = { "accent" }, Height = 34, MinWidth = 84, IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(PasswordInput.Text) ||
                (export && string.IsNullOrWhiteSpace(UidInput.Text) && string.IsNullOrWhiteSpace(EmailInput.Text)))
            {
                error.Text = strings[export ? "ExportRequired" : "RequiredPassword"];
                return;
            }
            Close(TakeInput());
        };
        var cancel = new Button
        {
            Content = strings["Cancel"],
            Height = 34,
            MinWidth = 76,
            Padding = new Thickness(14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Close();
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok }
        });

        Content = body;
        Opened += (_, _) => (export ? UidInput : PasswordInput).Focus();
        Closed += (_, _) => PasswordInput.Text = string.Empty;
    }

    internal SecretInput TakeInput()
    {
        int days = (ExpirationCombo.SelectedItem as ExpirationChoice)?.Days ?? 0;
        DateTimeOffset? expiresAt = days > 0 ? DateTimeOffset.UtcNow.AddDays(days) : null;
        var input = new SecretInput(
            UidInput.Text ?? "",
            EmailInput.Text ?? "",
            (PasswordInput.Text ?? "").ToCharArray(),
            IncludeEncryptSubkeyCheck.IsChecked == true,
            expiresAt);
        PasswordInput.Text = string.Empty;
        return input;
    }
}

internal sealed record SecretInput(
    string Uid,
    string Email,
    char[] Password,
    bool IncludeEncryptionSubkey = true,
    DateTimeOffset? ExpiresAtUtc = null);
