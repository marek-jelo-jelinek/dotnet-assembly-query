using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

internal sealed class CliOptions
{
    public string Command { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<string> Paths { get; } = [];
    public string SourceRoot { get; set; } = Directory.GetCurrentDirectory();
    public bool NoDaemon { get; set; }
    public int? DaemonIdleTimeoutSeconds { get; set; }
    public string? Kind { get; init; }
    public string? Namespace { get; init; }
    public string? AssemblyName { get; init; }
    public List<string>? FrameworkPaths { get; init; }
    public bool IncludeFrameworkResults { get; init; }
    public bool Contains { get; init; }
    public bool Json { get; init; }
    public bool Verbose { get; init; }
}

/// <summary>The <c>daq</c> command-line entry point: argument parsing, console output, exit codes.</summary>
public static class Cli
{
    /// <summary>
    /// The entry point invoked from <c>Program.cs</c>. Parses <paramref name="args"/> and either
    /// dispatches to the daemon worker loop or runs the CLI's root command, returning the process
    /// exit code.
    /// </summary>
    public static int Run(string[] args)
    {
        // Hidden entrypoint used only by daemon processes spawned by this same tool - never
        // documented, never reachable through the command-line grammar below.
        if (args.Length > 0 && args[0] == "__daemon-worker__")
        {
            return DaemonHost.RunWorkerLoop(args[1..], DispatchDaemonRequest);
        }

        var root = CliOptionsParser.BuildRootCommand(RunQuery, DispatchDaemonRequest);
        var parseResult = root.Parse(args);

        // ParseResult.Invoke() already prints parse errors plus auto-generated help to stderr and
        // returns 1 for them (same as for an unhandled exception) - remap to 2 so usage/parse
        // errors keep their own exit code, distinct from runtime failures.
        var invokeExitCode = parseResult.Invoke();
        return parseResult.Errors.Count > 0 ? 2 : invokeExitCode;
    }

    private static int RunQuery(CliOptions options)
    {
        List<string> dllPaths;
        try
        {
            dllPaths = AssemblyLoading.DiscoverDllPaths(options.Paths, out var discoveryWarnings);
            foreach (var warning in discoveryWarnings)
            {
                if (!options.Verbose && warning.VerboseOnly) continue;
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

        if (!options.NoDaemon)
        {
            var request = new DaemonRequest(
                Command: options.Command,
                Name: options.Name,
                SourceRoot: options.SourceRoot,
                DllPaths: dllPaths,
                Kind: options.Kind,
                Namespace: options.Namespace,
                AssemblyName: options.AssemblyName,
                Json: options.Json,
                FrameworkPaths: options.FrameworkPaths,
                IncludeFrameworkResults: options.IncludeFrameworkResults,
                Contains: options.Contains);
            if (DaemonClient.TryRun(request, out var daemonExitCode))
            {
                return daemonExitCode;
            }
        }

        var modules = new List<ModuleDefinition>();
        var frameworkModules = new List<ModuleDefinition>();
        // Shared across the modules load below and LoadAutoFrameworkTypes' separate load, so a
        // user type's reference into the framework/override set (or vice versa) can resolve -
        // see AssemblyLoading.LoadModules(IReadOnlyList{string}, DefaultAssemblyResolver, out List{string}).
        var resolver = new DefaultAssemblyResolver();
        int exitCode;
        try
        {
            modules.AddRange(AssemblyLoading.LoadModules(dllPaths, resolver, out var loadWarnings));
            foreach (var warning in loadWarnings)
            {
                Console.Error.WriteLine($"Warning: {warning}");
            }

            var allTypes = new List<TypeDefinition>();
            foreach (var module in modules)
            {
                allTypes.AddRange(module.GetTypes());
            }

            // One-shot (no daemon to cache across calls): load the framework/override directory's
            // types only if implementations --framework-path actually needs them.
            List<TypeDefinition> LoadAutoFrameworkTypes()
            {
                List<string> entries;
                if (options.FrameworkPaths is { Count: > 0 })
                {
                    entries = options.FrameworkPaths;
                }
                else
                {
                    if (!FrameworkDiscovery.TryLocateSharedFrameworkDirectory(modules, out var frameworkDir) || frameworkDir == null)
                    {
                        return [];
                    }

                    entries = [frameworkDir];
                }

                var paths = FrameworkDiscovery.ResolveAssemblyPaths(entries);
                frameworkModules.AddRange(AssemblyLoading.LoadModules(paths, resolver, out _));

                var types = new List<TypeDefinition>();
                foreach (var module in frameworkModules)
                {
                    types.AddRange(module.GetTypes());
                }

                return types;
            }

            exitCode = CliDispatch.Dispatch(options, modules, allTypes, options.FrameworkPaths != null ? LoadAutoFrameworkTypes : null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            exitCode = 1;
        }
        finally
        {
            foreach (var module in modules)
            {
                module.Dispose();
            }

            foreach (var module in frameworkModules)
            {
                module.Dispose();
            }
        }

        // A daemon spawn is a pure side effect appended after exitCode is already fixed above -
        // it can never change what this invocation reports back.
        if (!options.NoDaemon)
        {
            try
            {
                DaemonLauncher.SpawnDetached(dllPaths, options.DaemonIdleTimeoutSeconds);
            }
            catch (Exception ex)
            {
                if (Environment.GetEnvironmentVariable("DAQ_DEBUG") == "1")
                {
                    Console.Error.WriteLine($"Warning: failed to start daemon: {ex.Message}");
                }
            }
        }

        return exitCode;
    }

    /// <summary>Adapts a <see cref="DaemonRequest"/> (already validated over the wire) into a <see cref="CliOptions"/> and runs it against the daemon's resident modules.</summary>
    private static int DispatchDaemonRequest(DaemonRequest request, List<ModuleDefinition> modules, List<TypeDefinition> allTypes, Func<List<TypeDefinition>>? autoFrameworkTypes)
    {
        var options = new CliOptions
        {
            Command = request.Command,
            Name = request.Name,
            SourceRoot = request.SourceRoot,
            Kind = request.Kind,
            Namespace = request.Namespace,
            AssemblyName = request.AssemblyName,
            Json = request.Json,
            IncludeFrameworkResults = request.IncludeFrameworkResults,
            Contains = request.Contains,
        };
        return CliDispatch.Dispatch(options, modules, allTypes, autoFrameworkTypes);
    }
}
