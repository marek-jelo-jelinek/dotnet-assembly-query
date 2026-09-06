using System.CommandLine;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Builds daq's command-line grammar with System.CommandLine: a root command with one subcommand
/// per query kind (<c>find-symbol</c>, <c>hover</c>, <c>go-to-definition</c>, <c>find-references</c>)
/// plus a <c>daemon</c> command with <c>status</c>/<c>stop</c>/<c>start</c> subcommands. Query
/// subcommands share one set of options/arguments; <c>daemon start</c>/<c>daemon stop</c> only ever
/// add the subset of those they support, so passing e.g. <c>--source-root</c> to them is rejected
/// as an unrecognized option.
/// </summary>
internal static class CliOptionsParser
{
    private static readonly Argument<string> NameArgument = new("name")
    {
        Description = "Symbol name to search for.",
    };

    private static readonly Option<string[]> AssemblyOption = new("--assembly")
    {
        Description = "Assembly (.dll) to include (repeatable).",
        AllowMultipleArgumentsPerToken = false,
    };

    private static readonly Option<string[]> DirOption = new("--dir")
    {
        Description = "Directory to scan for .dll files (repeatable).",
        AllowMultipleArgumentsPerToken = false,
    };

    private static readonly Option<string> SourceRootOption = new("--source-root")
    {
        Description = "Root directory used to resolve/display source locations.",
        DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
    };

    private static readonly Option<bool> NoDaemonOption = new("--no-daemon")
    {
        Description = "Run in-process only; never try or spawn a background daemon.",
    };

    private static readonly Option<bool> JsonOption = new("--json")
    {
        Description = "Output machine-readable JSON instead of human-readable text.",
    };

    private static readonly Option<bool> ForegroundOption = new("--foreground")
    {
        Description = "Run the daemon in the foreground instead of detaching.",
    };

    private static readonly Option<string[]> FrameworkDirOption = new("--framework-dir")
    {
        Description = "If the target type isn't indexed, discover and load extra types to resolve it " +
            "(repeatable). Bare (no value): auto-discover the local machine's matching .NET shared " +
            "framework (Microsoft.NETCore.App). With one or more values: each value is a directory " +
            "or an explicit file path - mix freely.",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false,
    };

    private static readonly Option<bool> IncludeFrameworkResultsOption = new("--include-framework-results")
    {
        Description = "Include implementers declared only in --framework-dir-loaded assemblies in the " +
            "reported results. By default, --framework-dir assemblies are used only to resolve the " +
            "target type and walk base-type/interface chains; only types from --assembly/--dir are " +
            "reported as implementers.",
    };

    private static readonly Option<int?> DaemonIdleTimeoutOption = BuildDaemonIdleTimeoutOption();

    private static readonly Option<string?> KindOption = BuildKindOption();

    private static readonly Option<string?> NamespaceOption = new("--namespace")
    {
        Description = "Restrict matches to this containing namespace.",
    };

    private static readonly Option<string?> AssemblyNameOption = new("--assembly-name")
    {
        Description = "Restrict matches to this containing assembly name.",
    };

    /// <summary>
    /// Which optional pieces of a query subcommand's grammar apply - subcommands differ in
    /// whether they take a positional name, a <c>--kind</c> filter, and/or <c>--namespace</c>/
    /// <c>--assembly-name</c> filters, but otherwise share the same option set/action shape.
    /// </summary>
    [Flags]
    private enum SubcommandFeatures
    {
        None = 0,
        Name = 1 << 0,
        Kind = 1 << 1,
        Filters = 1 << 2, // --namespace + --assembly-name
        AutoFramework = 1 << 3,
    }

    /// <summary>Builds the root command; <paramref name="runQuery"/> is invoked once per matched query subcommand.</summary>
    public static RootCommand BuildRootCommand(Func<CliOptions, int> runQuery, DaemonDispatch daemonDispatch)
    {
        var root = new RootCommand(
            "A tiny, fast CLI that answers \"where is this symbol / what calls it\" by reading compiled " +
            ".NET assemblies and their PDBs - no build, no workspace, no decompiler.");

        const SubcommandFeatures nameKindFilters = SubcommandFeatures.Name | SubcommandFeatures.Kind | SubcommandFeatures.Filters;
        foreach (var name in new[] { "find-symbol", "search", "hover", "go-to-definition", "find-references" })
        {
            root.Subcommands.Add(BuildSubcommand(name, runQuery, nameKindFilters));
        }

        root.Subcommands.Add(BuildSubcommand("list-members", runQuery, nameKindFilters));
        root.Subcommands.Add(BuildSubcommand("implementations", runQuery, SubcommandFeatures.Name | SubcommandFeatures.Filters | SubcommandFeatures.AutoFramework));
        root.Subcommands.Add(BuildSubcommand("list-assemblies", runQuery, SubcommandFeatures.None));

        root.Subcommands.Add(BuildDaemonCommand(daemonDispatch));

        return root;
    }

    private static Command BuildSubcommand(string commandName, Func<CliOptions, int> runQuery, SubcommandFeatures features)
    {
        var command = new Command(commandName, Describe(commandName));
        if (features.HasFlag(SubcommandFeatures.Name)) command.Arguments.Add(NameArgument);
        command.Options.Add(AssemblyOption);
        command.Options.Add(DirOption);
        command.Options.Add(SourceRootOption);
        command.Options.Add(NoDaemonOption);
        command.Options.Add(DaemonIdleTimeoutOption);
        if (features.HasFlag(SubcommandFeatures.Kind)) command.Options.Add(KindOption);
        if (features.HasFlag(SubcommandFeatures.Filters))
        {
            command.Options.Add(NamespaceOption);
            command.Options.Add(AssemblyNameOption);
        }

        if (features.HasFlag(SubcommandFeatures.AutoFramework))
        {
            command.Options.Add(FrameworkDirOption);
            command.Options.Add(IncludeFrameworkResultsOption);
        }

        command.Options.Add(JsonOption);

        command.SetAction(parseResult =>
        {
            var options = new CliOptions
            {
                Command = commandName,
                Name = features.HasFlag(SubcommandFeatures.Name) ? parseResult.GetValue(NameArgument) ?? "" : "",
                SourceRoot = parseResult.GetValue(SourceRootOption) ?? Directory.GetCurrentDirectory(),
                NoDaemon = parseResult.GetValue(NoDaemonOption),
                DaemonIdleTimeoutSeconds = parseResult.GetValue(DaemonIdleTimeoutOption),
                Kind = features.HasFlag(SubcommandFeatures.Kind) ? parseResult.GetValue(KindOption) : null,
                Namespace = features.HasFlag(SubcommandFeatures.Filters) ? parseResult.GetValue(NamespaceOption) : null,
                AssemblyName = features.HasFlag(SubcommandFeatures.Filters) ? parseResult.GetValue(AssemblyNameOption) : null,
                FrameworkPaths = features.HasFlag(SubcommandFeatures.AutoFramework) && parseResult.GetResult(FrameworkDirOption) != null
                    ? [.. parseResult.GetValue(FrameworkDirOption) ?? []]
                    : null,
                IncludeFrameworkResults = features.HasFlag(SubcommandFeatures.AutoFramework) && parseResult.GetValue(IncludeFrameworkResultsOption),
                Json = parseResult.GetValue(JsonOption),
            };
            options.AssemblyPaths.AddRange(parseResult.GetValue(AssemblyOption) ?? []);
            options.Directories.AddRange(parseResult.GetValue(DirOption) ?? []);
            return runQuery(options);
        });

        return command;
    }

    private static Command BuildDaemonCommand(DaemonDispatch daemonDispatch)
    {
        var daemon = new Command("daemon", "Manage the background daemon that keeps assemblies resident between invocations.");

        var status = new Command("status", "List running daemons.");
        status.Options.Add(JsonOption);
        status.SetAction(parseResult => DaemonControl.Status(parseResult.GetValue(JsonOption)));

        var stop = new Command("stop", "Stop matching daemon(s), or all daemons if no filter is given.");
        stop.Options.Add(AssemblyOption);
        stop.Options.Add(DirOption);
        stop.Options.Add(JsonOption);
        stop.SetAction(parseResult => DaemonControl.Stop(
            [.. parseResult.GetValue(AssemblyOption) ?? []],
            [.. parseResult.GetValue(DirOption) ?? []],
            parseResult.GetValue(JsonOption)));

        var start = new Command("start", "Start a daemon for the given assemblies (or run it in the foreground).");
        start.Options.Add(AssemblyOption);
        start.Options.Add(DirOption);
        start.Options.Add(ForegroundOption);
        start.Options.Add(DaemonIdleTimeoutOption);
        start.Options.Add(JsonOption);
        start.SetAction(parseResult => DaemonControl.Start(
            [.. parseResult.GetValue(AssemblyOption) ?? []],
            [.. parseResult.GetValue(DirOption) ?? []],
            parseResult.GetValue(ForegroundOption),
            parseResult.GetValue(DaemonIdleTimeoutOption),
            parseResult.GetValue(JsonOption),
            daemonDispatch));

        daemon.Subcommands.Add(status);
        daemon.Subcommands.Add(stop);
        daemon.Subcommands.Add(start);
        return daemon;
    }

    private static Option<int?> BuildDaemonIdleTimeoutOption()
    {
        var option = new Option<int?>("--daemon-idle-timeout")
        {
            Description = "Idle timeout in seconds before an auto-spawned daemon exits.",
            Hidden = true,
        };
        option.Validators.Add(result =>
        {
            int? value;
            try
            {
                // Throws if the token failed to convert to int? at all (e.g. "not-a-number") -
                // System.CommandLine already reports that as its own error, so there's nothing
                // for this positive-number check to add in that case.
                value = result.GetValue(option);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (value is <= 0)
            {
                result.AddError($"--daemon-idle-timeout expects a positive number of seconds, got '{value}'.");
            }
        });
        return option;
    }

    private static Option<string?> BuildKindOption()
    {
        var option = new Option<string?>("--kind")
        {
            Description = "Restrict matches to this member kind: type, method, field, or property.",
        };
        option.Validators.Add(result =>
        {
            var value = result.GetValue(option);
            if (value != null && value is not ("type" or "method" or "field" or "property"))
            {
                result.AddError($"--kind expects one of 'type', 'method', 'field', 'property', got '{value}'.");
            }
        });
        return option;
    }

    private static string Describe(string commandName) => commandName switch
    {
        "find-symbol" => "Find all symbols matching <name>.",
        "search" => "Find all symbols whose name contains <name> (case-insensitive substring match).",
        "hover" => "Show the signature(s) of symbols matching <name>.",
        "go-to-definition" => "Show the source location of symbols matching <name>.",
        "find-references" => "Find references to symbols matching <name>.",
        "list-members" => "List the members (methods, fields, properties, nested types) of a type.",
        "implementations" => "Find types that implement or derive from an interface or base type.",
        "list-assemblies" => "List loaded assemblies.",
        _ => throw new ArgumentOutOfRangeException(nameof(commandName), commandName, null),
    };
}
