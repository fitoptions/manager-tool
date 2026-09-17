using System.Diagnostics;

namespace ManagerTool.Agent;

/// <summary>
/// Handles an admin-initiated remote uninstall. The agent runs non-elevated, so it cannot
/// remove a machine-wide install (Program Files + HKLM) itself. Instead the installer
/// pre-creates an elevated SYSTEM scheduled task ("ManagerToolRemove") that performs the full
/// cleanup; the agent merely triggers it and exits so files unlock. The task's own script
/// terminates the agent as a backstop and then deletes everything, including itself.
/// </summary>
internal static class Uninstaller
{
    private const string RemovalTask = "ManagerToolRemove";

    public static void Run()
    {
        // Trigger the elevated removal task created at install time.
        TryStart("schtasks.exe", $"/Run /TN {RemovalTask}");

        // Exit so the uninstaller can delete our binaries. The task's taskkill is the backstop
        // if we don't exit in time.
        try
        {
            var app = System.Windows.Application.Current;
            if (app is not null)
                app.Dispatcher.Invoke(() => app.Shutdown());
            else
                Environment.Exit(0);
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    private static void TryStart(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
        }
        catch
        {
            // If the task is missing (e.g. an install method that didn't create it), there is
            // nothing safe a non-elevated process can do; the admin can fall back to the script.
        }
    }
}
