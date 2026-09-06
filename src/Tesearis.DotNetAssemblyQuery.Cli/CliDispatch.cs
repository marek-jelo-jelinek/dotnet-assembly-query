using System.Text.Json;
using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Runs a parsed command against already-loaded modules/types. Shared by <see cref="Cli.Run"/>
/// (in-process) and <see cref="DaemonHost"/> (per-request, against a daemon's resident modules) -
/// this is the single place command dispatch and its crash guard live, so both callers behave
/// identically.
/// </summary>
internal static class CliDispatch
{
    public static int Dispatch(CliOptions options, List<ModuleDefinition> modules, List<TypeDefinition> allTypes, Func<List<TypeDefinition>>? autoFrameworkTypes = null)
    {
        try
        {
            return options.Command switch
            {
                "find-symbol" => PrintFindSymbol(allTypes, options.Name, options.Kind, options.Namespace, options.AssemblyName, options.Json),
                "search" => PrintSearch(allTypes, options.Name, options.Kind, options.Namespace, options.AssemblyName, options.Json),
                "hover" => PrintHover(allTypes, options.Name, options.Kind, options.Namespace, options.AssemblyName, options.Json),
                "go-to-definition" => PrintGoToDefinition(allTypes, options.Name, options.SourceRoot, options.Kind, options.Namespace, options.AssemblyName, options.Json),
                "find-references" => PrintFindReferences(modules, allTypes, options.Name, options.SourceRoot, options.Kind, options.Namespace, options.AssemblyName, options.Json),
                "list-members" => PrintListMembers(allTypes, options.Name, options.Kind, options.Namespace, options.AssemblyName, options.Json),
                "implementations" => PrintImplementations(allTypes, options.Name, options.Namespace, options.AssemblyName, options.Json, autoFrameworkTypes),
                "list-assemblies" => PrintListAssemblies(modules, options.Json),
                _ => UnknownCommand(options.Command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int PrintFindSymbol(List<TypeDefinition> allTypes, string name, string? kind, string? @namespace, string? assemblyName, bool json)
    {
        var matches = AssemblyQuery.FindSymbol(allTypes, name, kind, @namespace, assemblyName);

        if (json)
        {
            var results = matches.Select(m => new FindSymbolResultJson(Output.Kind(m), m.FullName, Output.AssemblyName(m))).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListFindSymbolResultJson));
            return 0;
        }

        if (matches.Count == 0)
        {
            Console.WriteLine($"No symbol named '{name}' found.");
            return 0;
        }

        foreach (var member in matches)
        {
            Console.WriteLine($"{Output.Kind(member)} {member.FullName} ({Output.AssemblyName(member)})");
        }

        return 0;
    }

    private static int PrintSearch(List<TypeDefinition> allTypes, string term, string? kind, string? @namespace, string? assemblyName, bool json)
    {
        var matches = AssemblyQuery.Search(allTypes, term, kind, @namespace, assemblyName);

        if (json)
        {
            var results = matches.Select(m => new SearchResultJson(Output.Kind(m), m.FullName, Output.AssemblyName(m))).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListSearchResultJson));
            return 0;
        }

        if (matches.Count == 0)
        {
            Console.WriteLine($"No symbol containing '{term}' found.");
            return 0;
        }

        foreach (var member in matches)
        {
            Console.WriteLine($"{Output.Kind(member)} {member.FullName} ({Output.AssemblyName(member)})");
        }

        return 0;
    }

    private static int PrintHover(List<TypeDefinition> allTypes, string name, string? kind, string? @namespace, string? assemblyName, bool json)
    {
        var signatures = AssemblyQuery.Hover(allTypes, name, kind, @namespace, assemblyName);

        if (json)
        {
            var results = signatures.Select(s => new HoverResultJson(s)).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListHoverResultJson));
            return 0;
        }

        if (signatures.Count == 0)
        {
            Console.WriteLine($"No symbol named '{name}' found.");
            return 0;
        }

        foreach (var signature in signatures)
        {
            Console.WriteLine(signature);
        }

        return 0;
    }

    private static int PrintGoToDefinition(List<TypeDefinition> allTypes, string name, string sourceRoot, string? kind, string? @namespace, string? assemblyName, bool json)
    {
        var matches = AssemblyQuery.GoToDefinition(allTypes, name, sourceRoot, kind, @namespace, assemblyName);

        if (json)
        {
            var results = matches.Select(m => new GoToDefinitionResultJson(
                Output.Kind(m.Member),
                Output.QualifiedName(m.Member),
                Output.AssemblyName(m.Member),
                m.Location != null ? new SourceLocationJson(m.Location.Path, m.Location.Line, m.Location.IsApproximate) : null,
                m.Location == null ? $"no source location (assembly '{Output.AssemblyName(m.Member)}' has no usable PDB, or member has no sequence points)" : null)).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListGoToDefinitionResultJson));
            return 0;
        }

        if (matches.Count == 0)
        {
            Console.WriteLine($"No symbol named '{name}' found.");
            return 0;
        }

        foreach (var (member, location) in matches)
        {
            Console.WriteLine(location != null
                ? $"{Output.Kind(member)} {Output.QualifiedName(member)} -> {location}"
                : $"{Output.Kind(member)} {Output.QualifiedName(member)} -> no source location (assembly '{Output.AssemblyName(member)}' has no usable PDB, or member has no sequence points)");
        }

        return 0;
    }

    private static int PrintFindReferences(List<ModuleDefinition> modules, List<TypeDefinition> allTypes, string name, string sourceRoot, string? kind, string? @namespace, string? assemblyName, bool json)
    {
        var targets = AssemblyQuery.FindSymbol(allTypes, name, kind, @namespace, assemblyName);
        if (targets.Count == 0)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new List<FindReferenceResultJson>(), CliOutputJsonContext.Default.ListFindReferenceResultJson));
                return 0;
            }

            Console.WriteLine($"No symbol named '{name}' found.");
            return 0;
        }

        var sites = AssemblyQuery.FindReferences(modules, allTypes, targets, sourceRoot);

        if (json)
        {
            var results = sites.Select(site => new FindReferenceResultJson(
                site.Site.FullName,
                site.Kind,
                site.IsTypePositionUsage,
                site.Location != null ? new SourceLocationJson(site.Location.Path, site.Location.Line, site.Location.IsApproximate) : null)).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListFindReferenceResultJson));
            return 0;
        }

        if (sites.Count == 0)
        {
            Console.WriteLine("No references found.");
            return 0;
        }

        foreach (var site in sites)
        {
            var member = site.Site;
            Console.WriteLine(site.IsTypePositionUsage
                ? site.Location != null
                    ? $"{member.FullName} ({site.Kind}) -> {site.Location}"
                    : $"{member.FullName} ({site.Kind}) (no source location available)"
                : site.Location != null
                    ? $"{member.FullName} -> {site.Location}"
                    : $"{member.FullName} (no source location available)");
        }

        return 0;
    }

    private static int PrintListMembers(List<TypeDefinition> allTypes, string name, string? kind, string? @namespace, string? assemblyName, bool json)
    {
        var members = AssemblyQuery.ListMembers(allTypes, name, kind, @namespace, assemblyName);

        if (json)
        {
            var results = members.Select(m => new ListMembersResultJson(Output.Kind(m), m.FullName, Output.AssemblyName(m))).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListListMembersResultJson));
            return 0;
        }

        if (members.Count == 0)
        {
            Console.WriteLine($"No members found for type '{name}'.");
            return 0;
        }

        foreach (var member in members)
        {
            Console.WriteLine($"{Output.Kind(member)} {member.FullName} ({Output.AssemblyName(member)})");
        }

        return 0;
    }

    private static int PrintImplementations(List<TypeDefinition> allTypes, string name, string? @namespace, string? assemblyName, bool json, Func<List<TypeDefinition>>? autoFrameworkTypes)
    {
        // Distinguish "the type itself isn't indexed" from "it's indexed but has no
        // implementers" - both would otherwise collapse into an empty match list and
        // print a misleading "no implementations" for e.g. BCL types like IDisposable
        // whose defining assembly (System.Private.CoreLib.dll) usually isn't indexed.
        var targetExists = AssemblyQuery.FindSymbol(allTypes, name, "type", @namespace, assemblyName).Count > 0;

        var autoFrameworkAttempted = false;
        if (!targetExists && autoFrameworkTypes != null)
        {
            autoFrameworkAttempted = true;
            var frameworkTypes = autoFrameworkTypes();
            if (frameworkTypes.Count > 0)
            {
                allTypes = [.. allTypes, .. frameworkTypes];
                targetExists = AssemblyQuery.FindSymbol(allTypes, name, "type", @namespace, assemblyName).Count > 0;
            }
        }

        var matches = AssemblyQuery.Implementations(allTypes, name, @namespace, assemblyName);

        if (json)
        {
            var results = matches.Select(t => new ImplementationsResultJson(Output.Kind(t), t.FullName, Output.AssemblyName(t))).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListImplementationsResultJson));
            return 0;
        }

        if (!targetExists)
        {
            var hint = autoFrameworkAttempted
                ? $"Type '{name}' was not found in the indexed assemblies, and --framework-dir couldn't locate/resolve it either."
                : $"Type '{name}' was not found in the indexed assemblies. If it's a framework/BCL type (e.g. IDisposable), index its defining assembly too (e.g. add System.Private.CoreLib.dll via --assembly or --dir), or retry with --framework-dir (bare, to auto-discover the local .NET shared framework, or with a directory/file path for a non-dotnet-SDK framework).";
            Console.WriteLine(hint);
            return 0;
        }

        if (matches.Count == 0)
        {
            Console.WriteLine($"No implementations of '{name}' found.");
            return 0;
        }

        foreach (var type in matches)
        {
            Console.WriteLine($"{Output.Kind(type)} {type.FullName} ({Output.AssemblyName(type)})");
        }

        return 0;
    }

    private static int PrintListAssemblies(List<ModuleDefinition> modules, bool json)
    {
        var assemblies = AssemblyQuery.ListAssemblies(modules);

        if (json)
        {
            var results = assemblies.Select(a => new ListAssembliesResultJson(a.Name, a.Version, a.FilePath)).ToList();
            Console.WriteLine(JsonSerializer.Serialize(results, CliOutputJsonContext.Default.ListListAssembliesResultJson));
            return 0;
        }

        if (assemblies.Count == 0)
        {
            Console.WriteLine("No assemblies loaded.");
            return 0;
        }

        foreach (var assembly in assemblies)
        {
            Console.WriteLine($"{assembly.Name} {assembly.Version} ({assembly.FilePath})");
        }

        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return 2;
    }
}
