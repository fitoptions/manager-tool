using System.Windows;
using System.Windows.Media;

namespace ManagerTool.Admin;

/// <summary>
/// Second step of a forgotten-password recovery: enter the emailed code and choose a new password.
///
/// The wording never asserts that an email was actually sent — the server does not tell us whether
/// the account exists, and this window must not imply otherwise.
/// </summary>
public partial class OtpResetWindow : Window
{
    private readonly string _serverUrl;
    private readonly string _username;
    private readonly bool _allowInvalidCert;

    public OtpResetWindow(string serverUrl, string username, bool allowInvalidCert)
    {
        InitializeComponent();
        _serverUrl = serverUrl;
        _username = username;
        _allowInvalidCert = allowInvalidCert;

        Intro.Text = $"If {username} is a registered account, a 6-digit code is on its way to that " +
                     "address. It expires in 10 minutes and can be used once.";
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void Validate(object sender, RoutedEventArgs e)
    {
        var matches = NewBox.Password == ConfirmBox.Password;
        SubmitButton.IsEnabled = CodeBox.Text.Trim().Length > 0
                                 && NewBox.Password.Length >= 8
                                 && matches;

        if (ConfirmBox.Password.Length > 0 && !matches)
        {
            Message.Foreground = Brushes.IndianRed;
            Message.Text = "The two new passwords do not match.";
        }
        else if (Message.Foreground == Brushes.IndianRed)
        {
            Message.Text = "";
        }
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        ResendButton.IsEnabled = false;
        var sent = await AdminConnection.RequestPasswordResetAsync(_serverUrl, _username, _allowInvalidCert);
        ResendButton.IsEnabled = true;

        if (sent is null)
        {
            Message.Foreground = Brushes.IndianRed;
            Message.Text = "Could not reach the server.";
            return;
        }
        Message.Foreground = Brushes.MediumSeaGreen;
        Message.Text = "A new code has been sent. The previous one no longer works.";
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (NewBox.Password != ConfirmBox.Password)
            return;

        SubmitButton.IsEnabled = false;
        var error = await AdminConnection.CompletePasswordResetAsync(
            _serverUrl, _username, CodeBox.Text.Trim(), NewBox.Password, _allowInvalidCert);

        if (error is not null)
        {
            Message.Foreground = Brushes.IndianRed;
            Message.Text = error;
            SubmitButton.IsEnabled = true;
            return;
        }

        DialogResult = true;   // login window tells them to sign in with the new password
    }
}
