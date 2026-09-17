using System.Windows;

namespace ManagerTool.Admin;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var login = new LoginWindow();
        var result = login.ShowDialog();

        if (result != true || login.Connection is null)
        {
            Shutdown();
            return;
        }

        var main = new MainWindow(login.Connection);
        MainWindow = main;
        main.Show();

        // Once the console is up, closing it ends the app.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }
}
