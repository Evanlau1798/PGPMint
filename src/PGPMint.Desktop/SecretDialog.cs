using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;

namespace PGPMint.Desktop;

public sealed class SecretDialog : Window
{
    internal TextBox PasswordInput { get; } = new() { PasswordChar = '●' };
    internal TextBox UidInput { get; } = new();
    internal TextBox EmailInput { get; } = new();
    public SecretDialog(LocalizationService strings, bool export = false)
    {
        Title = strings[export ? "ExportTitle" : "Unlock"];
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var body = new StackPanel { Margin = new(24), Spacing = 12 };
        void Input(string label, TextBox input)
        {
            body.Children.Add(new TextBlock { Text = label });
            AutomationProperties.SetName(input, label);
            body.Children.Add(input);
        }
        if (export)
        {
            Input(strings["NamePlaceholder"], UidInput);
            Input(strings["EmailPlaceholder"], EmailInput);
        }
        else body.Children.Add(new TextBlock { Text = strings["VaultHint"], TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Input(strings[export ? "ExportPassword" : "VaultPassword"], PasswordInput);
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        body.Children.Add(error);
        var ok = new Button { Content = strings[export ? "Save" : "Unlock"], IsDefault = true };
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
        var cancel = new Button { Content = strings["Cancel"], IsCancel = true };
        cancel.Click += (_, _) => Close();
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { ok, cancel } });
        Content = body;
        Closed += (_, _) => PasswordInput.Text = string.Empty;
    }

    internal SecretInput TakeInput()
    {
        var input = new SecretInput(UidInput.Text ?? "", EmailInput.Text ?? "", (PasswordInput.Text ?? "").ToCharArray());
        PasswordInput.Text = string.Empty;
        return input;
    }
}

internal sealed record SecretInput(string Uid, string Email, char[] Password);
