using System.Windows;
using System.Windows.Media;

namespace ManagerTool.Admin;

/// <summary>
/// Self-service password change for whoever is signed in. Open to every role: a viewer rotates
/// their own credentials without going through the owner. The current password is required, so
/// this is a change rather than a reset — a forgotten password still needs an owner reset.
/// </summary>
public partial class AccountWindow : Window
{
    private readonly AdminConnection _conn;

    public AccountWindow(AdminConnection connection)
    {
        InitializeComponent();
        _conn = connection;
        WhoAmI.Text = " " + _conn.Username;
        Loaded += (_, _) => CurrentBox.Focus();
    }

    /// <summary>Gate the button until the new password is long enough and both entries agree.</summary>
    private void NewBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var next = NewBox.Password;
        var matches = next == ConfirmBox.Password;

        UpdateButton.IsEnabled = next.Length >= 8 && matches;

        if (ConfirmBox.Password.Length == 0)
            Message.Text = "";
        else if (!matches)
        {
            Message.Foreground = Brushes.IndianRed;
            Message.Text = "The two new passwords do not match.";
        }
        else if (next.Length < 8)
        {
            Message.Foreground = Brushes.IndianRed;
            Message.Text = "New password must be at least 8 characters.";
        }
        else Message.Text = "";
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        Message.Foreground = Brushes.IndianRed;
        if (NewBox.Password != ConfirmBox.Password)
        {
            Message.Text = "The two new passwords do not match.";
            return;
        }

        UpdateButton.IsEnabled = false;
        var error = await _conn.ChangePasswordAsync(CurrentBox.Password, NewBox.Password);
        if (error is not null)
        {
            Message.Text = error;
            UpdateButton.IsEnabled = true;
            return;
        }

        // Clear first: emptying the boxes raises PasswordChanged, which resets Message — so the
        // confirmation has to be written afterwards or the operator never sees it.
        CurrentBox.Clear();
        NewBox.Clear();
        ConfirmBox.Clear();

        Message.Foreground = Brushes.MediumSeaGreen;
        Message.Text = "Password updated. Use the new one next time you sign in.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
