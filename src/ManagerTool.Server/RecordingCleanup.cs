namespace ManagerTool.Server;

/// <summary>
/// Deletes recorded-session folders whose last-write time is older than the cutoff.
/// Session folders live two levels under the root: {root}/{hostId}/{startStamp}/.
/// </summary>
public static class RecordingCleanup
{
    public static int PurgeOlderThan(string root, DateTimeOffset cutoffUtc)
    {
        if (!Directory.Exists(root))
            return 0;

        var removed = 0;
        foreach (var hostDir in Directory.GetDirectories(root))
        {
            foreach (var sessionDir in Directory.GetDirectories(hostDir))
            {
                // Use last-write so an in-progress recording (recently written) is never purged.
                var lastWrite = Directory.GetLastWriteTimeUtc(sessionDir);
                if (lastWrite < cutoffUtc.UtcDateTime)
                {
                    try
                    {
                        Directory.Delete(sessionDir, recursive: true);
                        removed++;
                    }
                    catch { /* locked / in use: skip, retry next sweep */ }
                }
            }
        }
        return removed;
    }
}
