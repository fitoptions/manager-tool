using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ManagerTool.Watchdog;

// ⚠ UNVERIFIED until compiled + run on a real machine. This is Win32 session/token interop
//   (launch a process into the active user session from a SYSTEM service). The logic is the
//   standard, well-trodden pattern, but it MUST be built and smoke-tested in a fresh session
//   before you trust it in production. See WATCHDOG-SETUP.md.

internal static class Program
{
    private static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "ManagerToolSvc");
        builder.Services.AddHostedService<Guardian>();        // keeps the agent alive in the session
        builder.Services.AddHostedService<UpdateChecker>();   // owns self-update (no scheduled task)
        builder.Build().Run();
    }
}

/// <summary>
/// Every few seconds: if no agent process is running in the active console session, launch one
/// there. Killing the agent therefore just causes a relaunch within one interval. The loop never
/// throws out of itself — a transient failure is retried on the next tick.
/// </summary>
internal sealed class Guardian : BackgroundService
{
    // Where the installer placed the agent. Keep in sync with the installer's DefaultDirName.
    private const string AgentExePath = @"C:\Program Files\Manager Tool\ManagerTool.exe";
    private const string AgentProcessName = "ManagerTool";          // no .exe
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var session = NativeMethods.WTSGetActiveConsoleSessionId();
                // 0xFFFFFFFF = no one attached to the console (e.g. at the lock/login screen).
                if (session != 0xFFFFFFFF && !IsAgentRunningIn(session) && File.Exists(AgentExePath))
                    NativeMethods.TryLaunchInSession(session, AgentExePath);
            }
            catch
            {
                // Never let a bad tick kill the guardian; SCM would restart us anyway, but we
                // prefer to keep running and retry on the next interval.
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static bool IsAgentRunningIn(uint sessionId)
    {
        foreach (var p in Process.GetProcessesByName(AgentProcessName))
        {
            try { if ((uint)p.SessionId == sessionId) return true; }
            catch { /* process exited between enumerate and read */ }
            finally { p.Dispose(); }
        }
        return false;
    }
}

/// <summary>
/// Polls the server on a slow cadence and, when this host is due, performs the whole update in-process
/// as SYSTEM (see <see cref="AgentUpdater"/>). Replaces the fragile agent -> scheduled-task -> ps1 hop.
/// </summary>
internal sealed class UpdateChecker : BackgroundService
{
    private static readonly TimeSpan First = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(3);

    private readonly AgentUpdater _updater =
        new(typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(First, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await _updater.CheckAsync(stoppingToken);   // best-effort; never throws out
            try { await Task.Delay(Every, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>P/Invoke to run a process in the interactive user's session from a SYSTEM service.</summary>
internal static class NativeMethods
{
    private const int TOKEN_ALL_ACCESS = 0x000F01FF;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint INVALID_SESSION = 0xFFFFFFFF;

    private const int SecurityImpersonation = 2;   // SECURITY_IMPERSONATION_LEVEL
    private const int TokenPrimary = 1;            // TOKEN_TYPE

    public static bool TryLaunchInSession(uint sessionId, string exePath)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken))
            return false;

        var primary = IntPtr.Zero;
        var env = IntPtr.Zero;
        try
        {
            var sa = new SECURITY_ATTRIBUTES();
            sa.nLength = Marshal.SizeOf(sa);

            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, ref sa,
                    SecurityImpersonation, TokenPrimary, out primary))
                return false;

            if (!CreateEnvironmentBlock(out env, primary, false))
                env = IntPtr.Zero;   // proceed without a fully-populated environment

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = @"winsta0\default";   // the interactive desktop

            var ok = CreateProcessAsUser(
                primary, exePath, null,
                IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT, env,
                Path.GetDirectoryName(exePath), ref si, out var pi);

            if (ok)
            {
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
            }
            return ok;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, int dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpTokenAttributes, int impersonationLevel, int tokenType,
        out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? applicationName,
        string? commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        int creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}
