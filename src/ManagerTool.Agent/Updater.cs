using System.Diagnostics;

namespace ManagerTool.Agent;

/// <summary>
/// Handles an admin-initiated remote update. The non-elevated agent does NOT download or place
/// any file that a privileged task later executes (which would be a privilege-escalation risk).
/// It simply triggers the pre-created SYSTEM task "ManagerToolUpdate", whose script — living in a
/// locked, admin-only directory — downloads the signed installer to that same locked directory
/// and runs it. No user-writable path is ever executed with elevation.
/// </summary>
internal static class Updater
{
    private const string UpdateTask = "ManagerToolUpdate";

    public static void Run()
    {
        try
        {
            Process.Start(new ProcessStartInfo("schtasks.exe", $"/Run /TN {UpdateTask}")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch
        {
            // Task missing (installed by a method that didn't create it) — nothing a
            // non-elevated process can safely do; admin falls back to a manual re-install.
        }
        // The installer will stop this process as part of the update; no self-exit needed.
    }
}
