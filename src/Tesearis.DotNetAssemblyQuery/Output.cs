using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Formats <see cref="IMemberDefinition"/>s for CLI output: names, kinds, and signatures.</summary>
public static class Output
{
    /// <summary>The name of the assembly containing <paramref name="member"/>.</summary>
    public static string AssemblyName(IMemberDefinition member)
    {
        return (member as TypeDefinition ?? member.DeclaringType)?.Module.Assembly.Name.Name ?? "?";
    }

    /// <summary>
    /// The containing namespace of <paramref name="member"/>. Mono.Cecil leaves
    /// <see cref="TypeReference.Namespace"/> empty on nested types, so this walks up to the
    /// outermost declaring type before reading it.
    /// </summary>
    public static string Namespace(IMemberDefinition member)
    {
        var type = member as TypeDefinition ?? member.DeclaringType;
        while (type?.DeclaringType != null)
        {
            type = type.DeclaringType;
        }

        return type?.Namespace ?? string.Empty;
    }

    /// <summary>A short member-kind label (<c>"class"</c>, <c>"method"</c>, <c>"field"</c>, etc.) for <paramref name="member"/>.</summary>
    public static string Kind(IMemberDefinition member) => member switch
    {
        TypeDefinition { IsInterface: true } => "interface",
        TypeDefinition { IsEnum: true } => "enum",
        TypeDefinition { IsValueType: true } => "struct",
        TypeDefinition => "class",
        MethodDefinition { IsConstructor: true } => "constructor",
        MethodDefinition => "method",
        FieldDefinition => "field",
        PropertyDefinition => "property",
        _ => "member",
    };

    /// <summary>A human-readable signature for <paramref name="member"/>, for hover/search output.</summary>
    public static string Signature(IMemberDefinition member) => member switch
    {
        TypeDefinition type => $"{Kind(type)} {type.FullName}" + (type.BaseType != null ? $" : {type.BaseType.FullName}" : ""),
        MethodDefinition method => $"{Visibility(method.IsPublic, method.IsPrivate, method.IsFamily, method.IsAssembly, method.IsFamilyOrAssembly, method.IsFamilyAndAssembly)} {method.ReturnType.FullName} {QualifiedName(method)}"
            + (method.PInvokeInfo != null ? $" // P/Invoke: {method.PInvokeInfo.Module.Name}!{method.PInvokeInfo.EntryPoint}" : ""),
        FieldDefinition field => $"{Visibility(field.IsPublic, field.IsPrivate, field.IsFamily, field.IsAssembly, field.IsFamilyOrAssembly, field.IsFamilyAndAssembly)} {field.FieldType.FullName} {field.FullName}",
        PropertyDefinition property => property.FullName,
        _ => member.FullName,
    };

    /// <summary>
    /// A member's fully-qualified name, suitable for disambiguating overloads. Unlike Cecil's
    /// own <see cref="IMemberDefinition.FullName"/> (which for a method is IL-style - return
    /// type first, "::" separator, no parameter names), this reads as a C#-ish
    /// <c>DeclaringType.MethodName(ParamType paramName, ...)</c>. Non-method members (types,
    /// fields, properties) aren't overloaded the same way, so they fall through to Cecil's
    /// <see cref="IMemberDefinition.FullName"/> unchanged.
    /// </summary>
    public static string QualifiedName(IMemberDefinition member) => member switch
    {
        MethodDefinition method => $"{method.DeclaringType.FullName}.{method.Name}{ParameterList(method)}",
        _ => member.FullName,
    };

    /// <summary>
    /// A method's parameter list as <c>(Type1 name1, Type2 name2)</c>, for overload
    /// disambiguation in hover/go-to-definition output.
    /// </summary>
    public static string ParameterList(MethodDefinition method)
    {
        return "(" + string.Join(", ", method.Parameters.Select(p => $"{p.ParameterType.FullName} {p.Name}")) + ")";
    }

    private static string Visibility(bool isPublic, bool isPrivate, bool isFamily, bool isAssembly, bool isFamilyOrAssembly, bool isFamilyAndAssembly)
    {
        if (isPublic) return "public";
        if (isPrivate) return "private";
        if (isFamilyOrAssembly) return "protected internal";
        if (isFamilyAndAssembly) return "private protected";
        if (isFamily) return "protected";
        return "internal"; // isAssembly, or no flags set on nested-private-by-default members
    }
}
