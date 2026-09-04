using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Name/kind matching, containment, and reference-resolution helpers over Mono.Cecil types.</summary>
public static class SymbolIndex
{
    /// <summary>Finds all types and members exactly named <paramref name="name"/> (types match on <c>Name</c> or <c>FullName</c>).</summary>
    public static IEnumerable<IMemberDefinition> MatchMembers(IReadOnlyList<TypeDefinition> allTypes, string name)
    {
        foreach (var type in allTypes)
        {
            if (type.Name == name || type.FullName == name) yield return type;

            foreach (var method in type.Methods)
            {
                if (method.Name == name) yield return method;
            }

            foreach (var field in type.Fields)
            {
                if (field.Name == name) yield return field;
            }

            foreach (var property in type.Properties)
            {
                if (property.Name == name) yield return property;
            }
        }
    }

    /// <summary>
    /// Like <see cref="MatchMembers"/>, but matches a case-insensitive substring of the simple
    /// name instead of an exact name (types are matched on <c>Name</c> only, not <c>FullName</c>).
    /// </summary>
    public static IEnumerable<IMemberDefinition> MatchMembersContaining(IReadOnlyList<TypeDefinition> allTypes, string term)
    {
        foreach (var type in allTypes)
        {
            if (Contains(type.Name, term)) yield return type;

            foreach (var method in type.Methods)
            {
                if (Contains(method.Name, term)) yield return method;
            }

            foreach (var field in type.Fields)
            {
                if (Contains(field.Name, term)) yield return field;
            }

            foreach (var property in type.Properties)
            {
                if (Contains(property.Name, term)) yield return property;
            }
        }
    }

    private static bool Contains(string name, string term) => name.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="member"/> belongs to the coarse <paramref name="kind"/> bucket
    /// (<c>"type"</c>/<c>"method"</c>/<c>"field"</c>/<c>"property"</c>) used by the CLI's
    /// <c>--kind</c> filter - deliberately coarser than <see cref="Output.Kind"/> (which further
    /// splits type into class/struct/enum/interface, and method into method/constructor).
    /// </summary>
    public static bool MatchesKind(IMemberDefinition member, string kind) => kind switch
    {
        "type" => member is TypeDefinition,
        "method" => member is MethodDefinition,
        "field" => member is FieldDefinition,
        "property" => member is PropertyDefinition,
        _ => false,
    };

    /// <summary>A type's methods (including constructors), fields, properties, and nested types.</summary>
    public static IEnumerable<IMemberDefinition> Members(TypeDefinition type)
    {
        foreach (var method in type.Methods) yield return method;
        foreach (var field in type.Fields) yield return field;
        foreach (var property in type.Properties) yield return property;
        foreach (var nested in type.NestedTypes) yield return nested;
    }

    /// <summary>
    /// Whether <paramref name="type"/> transitively derives from or implements any of
    /// <paramref name="targets"/> - walking the base-class chain and each type's (recursively
    /// resolved) interfaces, so a base class's interfaces and interface-extends-interface both
    /// count.
    /// </summary>
    public static bool DerivesFromAny(TypeDefinition type, IReadOnlyList<TypeDefinition> targets)
    {
        foreach (var target in targets)
        {
            if (DerivesFrom(type, target, [])) return true;
        }

        return false;
    }

    private static bool DerivesFrom(TypeDefinition type, TypeDefinition target, HashSet<TypeDefinition> visited)
    {
        if (!visited.Add(type)) return false;

        var baseType = type.BaseType != null ? SafeResolve(type.BaseType.Resolve) : null;
        if (baseType != null && (SameMember(baseType, target) || DerivesFrom(baseType, target, visited)))
        {
            return true;
        }

        foreach (var iface in type.Interfaces)
        {
            var ifaceType = SafeResolve(iface.InterfaceType.Resolve);
            if (ifaceType != null && (SameMember(ifaceType, target) || DerivesFrom(ifaceType, target, visited))) return true;
        }

        return false;
    }

    /// <summary>Names of <paramref name="targets"/>, for the cheap pre-filter in <see cref="ResolvesToAny"/>/<see cref="IsReferenceTo"/>.</summary>
    public static HashSet<string> TargetNames(IReadOnlyList<IMemberDefinition> targets)
    {
        var names = new HashSet<string>();
        foreach (var target in targets)
        {
            names.Add(target.Name);
            if (target is PropertyDefinition prop)
            {
                if (prop.GetMethod != null) names.Add(prop.GetMethod.Name);
                if (prop.SetMethod != null) names.Add(prop.SetMethod.Name);
            }
            else if (target is EventDefinition evt)
            {
                if (evt.AddMethod != null) names.Add(evt.AddMethod.Name);
                if (evt.RemoveMethod != null) names.Add(evt.RemoveMethod.Name);
            }
        }

        return names;
    }

    /// <summary>Whether <paramref name="typeRef"/> resolves to any of <paramref name="targets"/>.</summary>
    public static bool ResolvesToAny(TypeReference? typeRef, IReadOnlyList<IMemberDefinition> targets, HashSet<string> targetNames)
    {
        if (typeRef == null) return false;

        // Recursively inspect generic arguments (e.g. List<TargetType>).
        if (typeRef is GenericInstanceType git)
        {
            foreach (var arg in git.GenericArguments)
            {
                if (ResolvesToAny(arg, targets, targetNames)) return true;
            }

            if (ResolvesToAny(git.ElementType, targets, targetNames)) return true;
        }
        else if (typeRef is TypeSpecification typeSpec)
        {
            // Covers ArrayType (TargetType[]), ByReferenceType, PointerType, etc.
            if (ResolvesToAny(typeSpec.ElementType, targets, targetNames)) return true;
        }

        // Direct name match before calling the expensive Resolve().
        if (targetNames.Contains(typeRef.Name))
        {
            var resolved = SafeResolve(typeRef.Resolve);
            if (resolved != null && MatchesAnyTarget(targets, resolved)) return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="instruction"/>'s operand resolves to any of <paramref name="targets"/>.</summary>
    public static bool IsReferenceTo(Instruction instruction, IReadOnlyList<IMemberDefinition> targets, HashSet<string> targetNames)
    {
        if (instruction.Operand is not MemberReference memberRef) return false;

        if (memberRef is TypeReference tr)
        {
            return ResolvesToAny(tr, targets, targetNames);
        }

        if (!targetNames.Contains(memberRef.Name)) return false;

        IMemberDefinition? resolved = memberRef switch
        {
            MethodReference mr => SafeResolve(mr.Resolve),
            FieldReference fr => SafeResolve(fr.Resolve),
            _ => null,
        };

        return resolved != null && MatchesAnyTarget(targets, resolved);
    }

    private static bool MatchesAnyTarget(IReadOnlyList<IMemberDefinition> targets, IMemberDefinition resolved)
    {
        foreach (var target in targets)
        {
            if (SameMember(target, resolved)) return true;

            if (target is PropertyDefinition prop && resolved is MethodDefinition method)
            {
                if (prop.GetMethod != null && SameMember(prop.GetMethod, method)) return true;
                if (prop.SetMethod != null && SameMember(prop.SetMethod, method)) return true;
            }
            else if (target is EventDefinition evt && resolved is MethodDefinition evtMethod)
            {
                if (evt.AddMethod != null && SameMember(evt.AddMethod, evtMethod)) return true;
                if (evt.RemoveMethod != null && SameMember(evt.RemoveMethod, evtMethod)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> are the same logical member. The same
    /// member can come back as two distinct Cecil object instances, so this compares by identity
    /// (declaring assembly + full name) instead of reference equality.
    /// </summary>
    public static bool SameMember(IMemberDefinition a, IMemberDefinition b)
    {
        return a.FullName == b.FullName && Output.AssemblyName(a) == Output.AssemblyName(b);
    }

    /// <summary>Runs <paramref name="resolve"/>, returning <see langword="null"/> instead of throwing on failure.</summary>
    public static T? SafeResolve<T>(Func<T> resolve) where T : class
    {
        try
        {
            return resolve();
        }
        catch
        {
            return null;
        }
    }
}
