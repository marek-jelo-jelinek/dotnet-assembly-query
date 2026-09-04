using System.Diagnostics;
using System.Reflection;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Spawns a detached daemon worker process for a DLL set, as a side effect of an in-process (cache-miss) invocation.</summary>
public static class DaemonLauncher
{
    /// <summary>Launches a background worker process for <paramref name="dllPaths"/>, detached from this process's console.</summary>
    public static void SpawnDetached(List<string> dllPaths, int? idleTimeoutSeconds)
    {
        var (fileName, prefixArgs) = ResolveSelfInvocation();

        var args = new List<string>(prefixArgs)
        {
            "__daemon-worker__",
            (idleTimeoutSeconds ?? 0).ToString(),
        };
        args.AddRange(dllPaths);

        if (OperatingSystem.IsWindows() && WindowsProcessLauncher.TryStartDetached(fileName, args)) return;

        StartDetachedViaProcess(fileName, args);
    }

    /// <summary>Resolves how to re-invoke this same tool: directly (native apphost) or via the "dotnet" muxer (dev/global-tool-shim scenarios).</summary>
    private static (string FileName, List<string> PrefixArgs) ResolveSelfInvocation()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to determine the current process path.");

        var fileName = Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return (processPath, []);
        }

        var entryAssemblyLocation = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(entryAssemblyLocation))
        {
            throw new InvalidOperationException("Unable to determine the entry assembly location for re-invocation via 'dotnet'.");
        }

        return (processPath, [entryAssemblyLocation]);
    }

    private static void StartDetachedViaProcess(string fileName, List<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo);
        if (process == null) return;

        // Not inherited from the parent: close stdin immediately, and drain stdout/stderr to
        // /dev/null in the background so the child never blocks writing into a full pipe buffer
        // after this (short-lived) parent process has already exited. The drain tasks hold their
        // own reference to the underlying streams, so disposing the Process handle here (once this
        // method returns) doesn't cut them off - it only releases the OS handle to the (detached,
        // still-running) child process itself.
        process.StandardInput.Close();
        DrainToNull(process.StandardOutput.BaseStream);
        DrainToNull(process.StandardError.BaseStream);
    }

    private static void DrainToNull(Stream stream)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await stream.CopyToAsync(Stream.Null);
            }
            catch
            {
                // The parent process is about to exit anyway - nothing to report this to.
            }
        });
    }
}
