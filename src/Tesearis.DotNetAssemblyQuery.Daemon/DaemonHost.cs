using System.IO.Pipes;
using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// The worker loop run inside a spawned (or <c>daq daemon start --foreground</c>'d) daemon
/// process: acquires the single-instance lock for its DLL set, keeps modules resident, and
/// answers requests over a named pipe until an explicit stop or idle timeout.
/// </summary>
public static class DaemonHost
{
    private const string ShutdownCommand = "__shutdown__";
    private const int DefaultIdleTimeoutSeconds = 1800;

    /// <summary>
    /// Runs the daemon's accept loop: <paramref name="args"/> is <c>[idleTimeoutSeconds, ...dllPaths]</c>.
    /// Blocks until an explicit stop or idle timeout, dispatching each request via <paramref name="dispatch"/>.
    /// </summary>
    public static int RunWorkerLoop(string[] args, DaemonDispatch dispatch)
    {
        if (args.Length < 1 || !int.TryParse(args[0], out var idleTimeoutSeconds) || idleTimeoutSeconds <= 0)
        {
            idleTimeoutSeconds = ResolveDefaultIdleTimeoutSeconds();
        }

        var dllPaths = args[1..].ToList();
        if (dllPaths.Count == 0) return 1;

        var signature = DllSetSignature.Compute(dllPaths);
        using var lockHandle = DaemonLock.TryAcquire(signature);
        // Another process already won the race for this signature - exit quietly, not an error.
        if (lockHandle == null) return 0;

        var modules = new List<ModuleDefinition>();
        var fingerprints = new Dictionary<string, DllFingerprint>();
        try
        {
            ReloadModules(dllPaths, modules, fingerprints);
        }
        catch
        {
            DaemonLock.Release(lockHandle, signature);
            return 1;
        }

        var pipeName = DllSetSignature.PipeName(signature);
        DaemonRegistry.Write(new DaemonRegistryEntry(signature, pipeName, Environment.ProcessId, dllPaths, DateTime.UtcNow));

        NamedPipeServerStream? server = null;
        // Written by the idle timer's callback thread, read by the accept loop below - Interlocked
        // rather than a plain bool so the loop is guaranteed to observe the write promptly.
        var stopping = 0;
        using var idleTimer = new Timer(_ =>
        {
            Interlocked.Exchange(ref stopping, 1);
            try
            {
                server?.Dispose();
            }
            catch
            {
                // Racing with the accept loop's own disposal - fine either way.
            }
        }, null, TimeSpan.FromSeconds(idleTimeoutSeconds), Timeout.InfiniteTimeSpan);

        try
        {
            while (Interlocked.CompareExchange(ref stopping, 0, 0) == 0)
            {
                server = CreateServerStream(pipeName);
                try
                {
                    server.WaitForConnection();
                }
                catch
                {
                    break; // Disposed by the idle timer while waiting - time to shut down.
                }

                idleTimer.Change(TimeSpan.FromSeconds(idleTimeoutSeconds), Timeout.InfiniteTimeSpan);

                if (!HandleConnection(server, dllPaths, modules, fingerprints, dispatch))
                {
                    break; // Version mismatch or all backing files gone - self-terminate.
                }

                server.Dispose();
            }
        }
        finally
        {
            server?.Dispose();
            foreach (var module in modules)
            {
                module.Dispose();
            }

            DaemonRegistry.Delete(signature);
            DaemonLock.Release(lockHandle, signature);
        }

        return 0;
    }

    /// <summary>Handles exactly one request. Returns false when the daemon should stop serving after this connection.</summary>
    private static bool HandleConnection(NamedPipeServerStream server, List<string> dllPaths, List<ModuleDefinition> modules, Dictionary<string, DllFingerprint> fingerprints, DaemonDispatch dispatch)
    {
        try
        {
            var clientVersion = PipeFraming.ReadInt32(server);
            if (clientVersion != DaemonProtocolVersion.Current)
            {
                return false;
            }

            var request = PipeFraming.ReadJson(server, DaemonJsonContext.Default.DaemonRequest);

            if (request.Command == ShutdownCommand)
            {
                PipeFraming.WriteJson(server, new DaemonResponse(0, "", ""), DaemonJsonContext.Default.DaemonResponse);
                return false;
            }

            if (!AnyFileStillExists(dllPaths))
            {
                var response = new DaemonResponse(1, "", "Error: the DLL set this daemon was serving no longer exists on disk.\n");
                PipeFraming.WriteJson(server, response, DaemonJsonContext.Default.DaemonResponse);
                return false;
            }

            if (!FingerprintsMatch(dllPaths, fingerprints))
            {
                try
                {
                    ReloadModules(dllPaths, modules, fingerprints);
                }
                catch (Exception ex)
                {
                    var response = new DaemonResponse(1, "", $"Error: failed to reload changed assemblies: {ex.Message}\n");
                    PipeFraming.WriteJson(server, response, DaemonJsonContext.Default.DaemonResponse);
                    return true;
                }
            }

            var allTypes = new List<TypeDefinition>();
            foreach (var module in modules)
            {
                allTypes.AddRange(module.GetTypes());
            }

            var (exitCode, stdout, stderr) = RunDispatchCapturingOutput(dispatch, request, modules, allTypes);

            PipeFraming.WriteJson(server, new DaemonResponse(exitCode, stdout, stderr), DaemonJsonContext.Default.DaemonResponse);
            return true;
        }
        catch
        {
            // A malformed request or a client that disconnected mid-write - move on to the next
            // connection rather than tearing the whole daemon down over one bad client.
            return true;
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunDispatchCapturingOutput(DaemonDispatch dispatch, DaemonRequest request, List<ModuleDefinition> modules, List<TypeDefinition> allTypes)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);
        try
        {
            var exitCode = dispatch(request, modules, allTypes);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static NamedPipeServerStream CreateServerStream(string pipeName)
    {
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
    }

    private static void ReloadModules(List<string> dllPaths, List<ModuleDefinition> modules, Dictionary<string, DllFingerprint> fingerprints)
    {
        var freshModules = DaemonModuleLoading.LoadModulesInMemory(dllPaths, out _);

        foreach (var module in modules)
        {
            module.Dispose();
        }

        modules.Clear();
        modules.AddRange(freshModules);

        fingerprints.Clear();
        foreach (var dllPath in dllPaths)
        {
            fingerprints[dllPath] = DllFingerprint.Compute(dllPath);
        }
    }

    private static bool AnyFileStillExists(List<string> dllPaths)
    {
        foreach (var dllPath in dllPaths)
        {
            if (File.Exists(dllPath)) return true;
        }

        return false;
    }

    private static bool FingerprintsMatch(List<string> dllPaths, Dictionary<string, DllFingerprint> fingerprints)
    {
        foreach (var dllPath in dllPaths)
        {
            if (!fingerprints.TryGetValue(dllPath, out var recorded) || recorded != DllFingerprint.Compute(dllPath)) return false;
        }

        return true;
    }

    private static int ResolveDefaultIdleTimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("DAQ_DAEMON_IDLE_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : DefaultIdleTimeoutSeconds;
    }
}

/// <summary>A cheap freshness fingerprint for a DLL and its sibling PDB (if any) - a stat, not a hash.</summary>
internal readonly record struct DllFingerprint(long DllLength, DateTime DllLastWriteUtc, bool HasPdb, long PdbLength, DateTime PdbLastWriteUtc)
{
    public static DllFingerprint Compute(string dllPath)
    {
        if (!File.Exists(dllPath)) return default;

        var dllInfo = new FileInfo(dllPath);
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        var pdbInfo = File.Exists(pdbPath) ? new FileInfo(pdbPath) : null;

        return new DllFingerprint(
            dllInfo.Length,
            dllInfo.LastWriteTimeUtc,
            pdbInfo != null,
            pdbInfo?.Length ?? 0,
            pdbInfo?.LastWriteTimeUtc ?? default);
    }
}
