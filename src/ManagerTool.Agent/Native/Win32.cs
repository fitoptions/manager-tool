using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ManagerTool.Agent.Native;

/// <summary>
/// Minimal Win32 surface for reading the foreground window and its owning process.
/// Read-only inspection APIs — nothing here hooks input or injects.
/// </summary>
internal static class Win32
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public readonly record struct ForegroundInfo(string ProcessName, string WindowTitle, uint ProcessId, IntPtr Handle);

    public static ForegroundInfo? GetForeground()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
            return null;

        var len = GetWindowTextLength(hWnd);
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        var title = sb.ToString();

        GetWindowThreadProcessId(hWnd, out var pid);

        var processName = "unknown";
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch
        {
            // Process may have exited between the two calls; leave as "unknown".
        }

        return new ForegroundInfo(processName, title, pid, hWnd);
    }

    private static readonly HashSet<string> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi"
    };

    public static bool IsBrowser(string processName) => KnownBrowsers.Contains(processName);
}
