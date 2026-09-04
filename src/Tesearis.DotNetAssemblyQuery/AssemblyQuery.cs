using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Symbol lookup, hover, go-to-definition, and find-references over loaded assemblies.</summary>
public static class AssemblyQuery
{
    /// <summary>
    /// Finds all members named <paramref name="name"/>, optionally narrowed by member kind
    /// (<c>"type"</c>/<c>"method"</c>/<c>"field"</c>/<c>"property"</c>), containing namespace,
    /// and/or containing assembly name.
    /// </summary>
    public static IReadOnlyList<IMemberDefinition> FindSymbol(IReadOnlyList<TypeDefinition> allTypes, string name, string? kind = null, string? @namespace = null, string? assemblyName = null)
    {
        var matches = SymbolIndex.MatchMembers([.. allTypes], name);

        if (kind != null) matches = matches.Where(m => SymbolIndex.MatchesKind(m, kind));
        if (@namespace != null) matches = matches.Where(m => Output.Namespace(m) == @namespace);
        if (assemblyName != null) matches = matches.Where(m => Output.AssemblyName(m) == assemblyName);

        return [.. matches];
    }

    /// <summary>
    /// Finds all members whose simple name contains <paramref name="term"/> (case-insensitive
    /// substring match), optionally narrowed by member kind, containing namespace, and/or
    /// containing assembly name - same filters as <see cref="FindSymbol"/>, but for discovery
    /// when the caller only knows part of a name.
    /// </summary>
    public static IReadOnlyList<IMemberDefinition> Search(IReadOnlyList<TypeDefinition> allTypes, string term, string? kind = null, string? @namespace = null, string? assemblyName = null)
    {
        var matches = SymbolIndex.MatchMembersContaining([.. allTypes], term);

        if (kind != null) matches = matches.Where(m => SymbolIndex.MatchesKind(m, kind));
        if (@namespace != null) matches = matches.Where(m => Output.Namespace(m) == @namespace);
        if (assemblyName != null) matches = matches.Where(m => Output.AssemblyName(m) == assemblyName);

        return [.. matches];
    }

    /// <summary>Finds a symbol and returns matches formatted as signatures.</summary>
    public static IReadOnlyList<string> Hover(IReadOnlyList<TypeDefinition> allTypes, string name, string? kind = null, string? @namespace = null, string? assemblyName = null)
    {
        return [.. FindSymbol(allTypes, name, kind, @namespace, assemblyName).Select(Output.Signature)];
    }

    /// <summary>Finds a symbol and resolves each match's source location.</summary>
    public static IReadOnlyList<MemberDefinitionLocation> GoToDefinition(IReadOnlyList<TypeDefinition> allTypes, string name, string sourceRoot, string? kind = null, string? @namespace = null, string? assemblyName = null)
    {
        var results = new List<MemberDefinitionLocation>();
        foreach (var member in FindSymbol(allTypes, name, kind, @namespace, assemblyName))
        {
            results.Add(new MemberDefinitionLocation(member, SourceLocator.ResolveSourceLocation(member, sourceRoot)));
        }

        return results;
    }

    /// <summary>
    /// Lists the members (methods, fields, properties, nested types) of the type named
    /// <paramref name="name"/>, optionally narrowed by member kind (<c>"type"</c>/<c>"method"</c>/
    /// <c>"field"</c>/<c>"property"</c>), containing namespace, and/or containing assembly name -
    /// the latter two disambiguate which type is meant, same as in <see cref="FindSymbol"/>.
    /// </summary>
    public static IReadOnlyList<IMemberDefinition> ListMembers(IReadOnlyList<TypeDefinition> allTypes, string name, string? kind = null, string? @namespace = null, string? assemblyName = null)
    {
        var targetTypes = FindSymbol(allTypes, name, "type", @namespace, assemblyName).OfType<TypeDefinition>();
        var members = targetTypes.SelectMany(SymbolIndex.Members);

        if (kind != null) members = members.Where(m => SymbolIndex.MatchesKind(m, kind));

        return [.. members];
    }

    /// <summary>
    /// Finds every type that transitively derives from or implements the interface/base type
    /// named <paramref name="name"/>. <paramref name="namespace"/>/<paramref name="assemblyName"/>
    /// disambiguate which type <paramref name="name"/> refers to, same as in <see cref="FindSymbol"/>.
    /// </summary>
    public static IReadOnlyList<TypeDefinition> Implementations(IReadOnlyList<TypeDefinition> allTypes, string name, string? @namespace = null, string? assemblyName = null)
    {
        var targets = FindSymbol(allTypes, name, "type", @namespace, assemblyName).OfType<TypeDefinition>().ToList();
        if (targets.Count == 0) return [];

        return [.. allTypes.Where(type => SymbolIndex.DerivesFromAny(type, targets))];
    }

    /// <summary>Lists the assemblies backing <paramref name="modules"/>.</summary>
    public static IReadOnlyList<AssemblyInfo> ListAssemblies(IReadOnlyList<ModuleDefinition> modules)
    {
        return [.. modules.Select(m => new AssemblyInfo(m.Assembly.Name.Name, m.Assembly.Name.Version.ToString(), m.FileName))];
    }

    /// <summary>Finds a symbol and scans <paramref name="modules"/>/<paramref name="allTypes"/> for every site that references it.</summary>
    public static IReadOnlyList<ReferenceSite> FindReferences(IReadOnlyList<ModuleDefinition> modules, IReadOnlyList<TypeDefinition> allTypes, string name, string sourceRoot, string? kind = null, string? @namespace = null, string? assemblyName = null)
    {
        return FindReferences(modules, allTypes, FindSymbol(allTypes, name, kind, @namespace, assemblyName), sourceRoot);
    }

    /// <summary>
    /// Same as <see cref="FindReferences(IReadOnlyList{ModuleDefinition}, IReadOnlyList{TypeDefinition}, string, string, string?, string?, string?)"/>,
    /// but takes an already-resolved set of targets (e.g. from a prior <see cref="FindSymbol"/> call) instead of
    /// re-resolving the name, so callers that already looked the symbol up don't pay for a second scan.
    /// </summary>
    public static IReadOnlyList<ReferenceSite> FindReferences(IReadOnlyList<ModuleDefinition> modules, IReadOnlyList<TypeDefinition> allTypes, IReadOnlyList<IMemberDefinition> targets, string sourceRoot)
    {
        var targetNames = SymbolIndex.TargetNames(targets);
        var sites = new List<ReferenceSite>();

        // Pass 1: IL instructions inside method bodies.
        foreach (var module in modules)
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (!SymbolIndex.IsReferenceTo(instruction, targets, targetNames)) continue;

                        var location = SourceLocator.ResolveInstructionLocation(method, instruction, sourceRoot);
                        sites.Add(new ReferenceSite(method, instruction.OpCode.Name, location, IsTypePositionUsage: false));
                    }
                }
            }
        }

        // Pass 2: type-position usages (base types, interfaces, field/return/parameter types).
        foreach (var type in allTypes)
        {
            if (SymbolIndex.ResolvesToAny(type.BaseType, targets, targetNames))
            {
                sites.Add(Report(type, "base type"));
            }

            foreach (var iface in type.Interfaces)
            {
                if (SymbolIndex.ResolvesToAny(iface.InterfaceType, targets, targetNames))
                {
                    sites.Add(Report(type, "interface"));
                }
            }

            foreach (var field in type.Fields)
            {
                if (field.Name.StartsWith('<') && field.Name.Contains(">k__BackingField")) continue;

                if (SymbolIndex.ResolvesToAny(field.FieldType, targets, targetNames))
                {
                    sites.Add(Report(field, "field type"));
                }
            }

            foreach (var property in type.Properties)
            {
                if (SymbolIndex.ResolvesToAny(property.PropertyType, targets, targetNames))
                {
                    sites.Add(Report(property, "property type"));
                }
            }

            foreach (var method in type.Methods)
            {
                if (method.IsGetter || method.IsSetter) continue;

                if (SymbolIndex.ResolvesToAny(method.ReturnType, targets, targetNames))
                {
                    sites.Add(Report(method, "return type"));
                }

                foreach (var parameter in method.Parameters)
                {
                    if (SymbolIndex.ResolvesToAny(parameter.ParameterType, targets, targetNames))
                    {
                        sites.Add(Report(method, $"parameter type ({parameter.Name})"));
                    }
                }
            }
        }

        return sites;

        ReferenceSite Report(IMemberDefinition member, string kind)
        {
            return new ReferenceSite(member, kind, SourceLocator.ResolveSourceLocation(member, sourceRoot), IsTypePositionUsage: true);
        }
    }
}
