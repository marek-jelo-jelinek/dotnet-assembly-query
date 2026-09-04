using System.Text.Json;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>A live daemon's advertised identity, written by <see cref="DaemonHost"/> and read by <see cref="DaemonControl"/>.</summary>
internal sealed record DaemonRegistryEntry(string Signature, string PipeName, int ProcessId, List<string> DllPaths, DateTime StartedUtc);

/// <summary>
/// The on-disk directory of live daemons - one JSON file per DLL-set signature, under a
/// per-user local-app-data directory. Entries are informational (status/stop tooling); the
/// pipe name is fully derived from the signature, so a client never needs to read a registry
/// entry to find a daemon - only <c>daq daemon status/stop</c> does.
/// </summary>
internal static class DaemonRegistry
{
    public static string RegistryDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "daq", "daemons");

    public static void Write(DaemonRegistryEntry entry)
    {
        Directory.CreateDirectory(RegistryDirectory);
        var path = EntryPath(entry.Signature);
        var tmpPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = File.Create(tmpPath))
        {
            JsonSerializer.Serialize(stream, entry, DaemonJsonContext.Default.DaemonRegistryEntry);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    public static DaemonRegistryEntry? TryRead(string signature)
    {
        try
        {
            using var stream = File.OpenRead(EntryPath(signature));
            return JsonSerializer.Deserialize(stream, DaemonJsonContext.Default.DaemonRegistryEntry);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string signature)
    {
        try
        {
            File.Delete(EntryPath(signature));
        }
        catch
        {
            // Best-effort - a missing/locked registry file is not fatal to shutdown.
        }
    }

    public static List<DaemonRegistryEntry> ListEntries()
    {
        var entries = new List<DaemonRegistryEntry>();
        if (!Directory.Exists(RegistryDirectory)) return entries;

        foreach (var file in Directory.GetFiles(RegistryDirectory, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                var entry = JsonSerializer.Deserialize(stream, DaemonJsonContext.Default.DaemonRegistryEntry);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }
            catch
            {
                // Corrupt/partial entry - skip it rather than fail the whole listing.
            }
        }

        return entries;
    }

    private static string EntryPath(string signature)
    {
        return Path.Combine(RegistryDirectory, signature + ".json");
    }
}