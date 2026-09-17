using System.Windows;

namespace ManagerTool.Admin;

/// <summary>
/// Collects server URL + admin credentials, performs the login, and (on success)
/// exposes a connected AdminConnection for MainWindow to consume.
/// </summary>
public partial class LoginWindow : Window
{
    // DEV default: trust the self-signed dev cert. Set false in production builds.
    private const bool AllowInvalidCertDev = true;

    public AdminConnection? Connection { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UserBox.Focus();
    }

    /// <summary>
    /// Reports a lockout to the owner. The confirmation is deliberately non-committal — the server
    /// answers the same way whether or not the account exists, so this window must not imply
    /// otherwise.
    /// </summary>
    private async void ForgotLink_Click(object sender, RoutedEventArgs e)
    {
        var username = UserBox.Text.Trim();
        if (username.Length == 0)
        {
            ForgotText.Foreground = System.Windows.Media.Brushes.IndianRed;
            ForgotText.Text = "Type your username above first, then click again.";
            return;
        }

        var server = ServerBox.Text.Trim();
        ForgotLink.IsEnabled = false;
        var codeSent = await AdminConnection.RequestPasswordResetAsync(server, username, AllowInvalidCertDev);
        ForgotLink.IsEnabled = true;

        if (codeSent is null)
        {
            ForgotText.Foreground = System.Windows.Media.Brushes.IndianRed;
            ForgotText.Text = "Could not reach the server. Check the URL and try again.";
            return;
        }

        if (codeSent == false)
        {
            // No email channel for this account — it fell back to the owner's lockout queue.
            ForgotText.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
            ForgotText.Text = "If that account exists, the owner has been notified. Contact them for your new password.";
            return;
        }

        var dialog = new OtpResetWindow(server, username, AllowInvalidCertDev) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            PassBox.Clear();
            PassBox.Focus();
            ForgotText.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
            ForgotText.Text = "Password updated — sign in with your new password.";
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        LoginButton.IsEnabled = false;
        try
        {
            var conn = new AdminConnection(ServerBox.Text.Trim(), AllowInvalidCertDev);
            var ok = await conn.LoginAsync(UserBox.Text.Trim(), PassBox.Password);
            if (!ok)
            {
                ErrorText.Text = "Invalid username or password.";
                return;
            }

            await conn.StartAsync();
            Connection = conn;
            DialogResult = true;   // closes the dialog; MainWindow proceeds
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Could not connect: {ex.Message}";
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }
}
