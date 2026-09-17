using System.IO;

namespace ManagerTool.Agent;

/// <summary>
/// Produces a stable per-machine identifier, persisted once under ProgramData so it
/// survives reinstalls and user switches. Not security-sensitive; just a correlation key.
/// </summary>
public static class MachineId
{
    public static string Get()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ManagerTool");
        Directory.CreateDirectory(dir);

        var file = Path.Combine(dir, "host-id");
        if (File.Exists(file))
        {
            var existing = File.ReadAllText(file).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
                return existing;
        }

        var id = $"{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();
        File.WriteAllText(file, id);
        return id;
    }
}
