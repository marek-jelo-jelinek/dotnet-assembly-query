using System.IO.Pipes;
using System.Text.Json;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Handles the <c>daq daemon status|stop|start</c> subcommands.</summary>
public static class DaemonControl
{
    /// <summary>Lists live daemons from the registry, pruning stale entries whose process is gone.</summary>
    public static int Status(bool json)
    {
        var entries = DaemonRegistry.ListEntries();
        var liveEntries = new List<DaemonRegistryEntry>();
        foreach (var entry in entries)
        {
            if (!IsProcessAlive(entry.ProcessId))
            {
                DaemonRegistry.Delete(entry.Signature);
                continue;
            }

            liveEntries.Add(entry);
        }

        if (json)
        {
            var results = liveEntries.Select(entry => new DaemonStatusEntryJson(
                entry.Signature,
                entry.ProcessId,
                entry.DllPaths.Count,
                (DateTime.UtcNow - entry.StartedUtc).ToString(@"hh\:mm\:ss"))).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, DaemonControlJsonContext.Default.ListDaemonStatusEntryJson));
            return 0;
        }

        foreach (var entry in liveEntries)
        {
            var uptime = DateTime.UtcNow - entry.StartedUtc;
            Console.WriteLine($@"{entry.Signature}  pid={entry.ProcessId}  dlls={entry.DllPaths.Count}  uptime={uptime:hh\:mm\:ss}");
        }

        if (liveEntries.Count == 0)
        {
            Console.WriteLine("No daemons running.");
        }

        return 0;
    }

    /// <summary>Stops the daemon(s) matching <paramref name="paths"/>, or all of them if it's empty.</summary>
    public static int Stop(List<string> paths, bool json)
    {
        var entries = DaemonRegistry.ListEntries();
        if (paths.Count > 0)
        {
            var dllPaths = AssemblyLoading.DiscoverDllPaths(paths, out _);
            var signature = DllSetSignature.Compute(dllPaths);
            entries = entries.Where(e => e.Signature == signature).ToList();
        }

        if (entries.Count == 0)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new List<DaemonStopResultJson>(), DaemonControlJsonContext.Default.ListDaemonStopResultJson));
                return 0;
            }

            Console.WriteLine("No matching daemons running.");
            return 0;
        }

        if (json)
        {
            var results = entries.Select(entry => new DaemonStopResultJson(entry.Signature, entry.ProcessId, StopOne(entry))).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, DaemonControlJsonContext.Default.ListDaemonStopResultJson));
            return 0;
        }

        foreach (var entry in entries)
        {
            StopOne(entry);
            Console.WriteLine($"Stopped {entry.Signature} (pid={entry.ProcessId}).");
        }

        return 0;
    }

    /// <summary>Stops one daemon (gracefully via pipe, else a hard kill). Returns whether it stopped gracefully.</summary>
    private static bool StopOne(DaemonRegistryEntry entry)
    {
        var stoppedGracefully = false;
        try
        {
            using var client = new NamedPipeClientStream(".", entry.PipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(200);
            PipeFraming.WriteInt32(client, DaemonProtocolVersion.Current);
            PipeFraming.WriteJson(client, new DaemonRequest("__shutdown__", "", "", []), DaemonJsonContext.Default.DaemonRequest);
            PipeFraming.ReadJson(client, DaemonJsonContext.Default.DaemonResponse);
            stoppedGracefully = true;
        }
        catch
        {
            // Fall through to a hard kill below.
        }

        if (!stoppedGracefully)
        {
            try
            {
                System.Diagnostics.Process.GetProcessById(entry.ProcessId).Kill();
            }
            catch
            {
                // Already gone.
            }

            DaemonRegistry.Delete(entry.Signature);
            DaemonLock.DeleteLockFile(entry.Signature);
        }

        return stoppedGracefully;
    }

    /// <summary>Starts a daemon for the resolved DLL set, either in the foreground or spawned detached.</summary>
    public static int Start(List<string> paths, bool foreground, int? idleTimeoutSeconds, bool json, bool verbose, DaemonDispatch dispatch)
    {
        List<string> dllPaths;
        try
        {
            dllPaths = AssemblyLoading.DiscoverDllPaths(paths, out var warnings);
            foreach (var warning in warnings)
            {
                if (!verbose && warning.VerboseOnly) continue;
                Console.Error.WriteLine($"Warning: {warning.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        if (dllPaths.Count == 0)
        {
            Console.Error.WriteLine("No assemblies found (use --path, or run from a directory containing .dll files).");
            return 1;
        }

        if (foreground)
        {
            var workerArgs = new List<string> { (idleTimeoutSeconds ?? 0).ToString() };
            workerArgs.AddRange(dllPaths);
            return DaemonHost.RunWorkerLoop([.. workerArgs], dispatch);
        }

        var signature = DllSetSignature.Compute(dllPaths);
        if (IsAlreadyRunning(signature))
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new DaemonStartResultJson("already-running", signature), DaemonControlJsonContext.Default.DaemonStartResultJson));
                return 0;
            }

            Console.WriteLine("A daemon for this DLL set is already running.");
            return 0;
        }

        DaemonLauncher.SpawnDetached(dllPaths, idleTimeoutSeconds);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new DaemonStartResultJson("started", signature), DaemonControlJsonContext.Default.DaemonStartResultJson));
            return 0;
        }

        Console.WriteLine("Daemon started.");
        return 0;
    }

    private static bool IsAlreadyRunning(string signature)
    {
        var entry = DaemonRegistry.TryRead(signature);
        return entry != null && IsProcessAlive(entry.ProcessId);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
