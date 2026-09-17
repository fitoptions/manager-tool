using System.Windows;

namespace ManagerTool.Admin;

/// <summary>
/// Collects the replacement password when the owner resets a locked-out viewer. Shown in plain
/// text on purpose: the owner has to read it out or write it down to hand it over, and hiding it
/// behind a PasswordBox would only invite typos in a value nobody can recover afterwards.
/// </summary>
public partial class ResetPasswordPrompt : Window
{
    public string NewPassword => PasswordBoxPlain.Text;

    public ResetPasswordPrompt(string username, string suggested)
    {
        InitializeComponent();
        Prompt.Text = $"New password for “{username}”:";
        PasswordBoxPlain.Text = suggested;
        Loaded += (_, _) => { PasswordBoxPlain.Focus(); PasswordBoxPlain.SelectAll(); };
    }

    private void PasswordBoxPlain_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var tooShort = PasswordBoxPlain.Text.Length < 8;
        Hint.Text = tooShort ? "Must be at least 8 characters." : "";
        OkButton.IsEnabled = !tooShort;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PasswordBoxPlain.Text.Length < 8) return;
        DialogResult = true;
    }
}
