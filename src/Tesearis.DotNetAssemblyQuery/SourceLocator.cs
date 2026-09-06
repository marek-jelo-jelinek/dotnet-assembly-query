using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Resolves PDB sequence points into <see cref="SourceLocation"/>s for members and instructions.</summary>
public static class SourceLocator
{
    /// <summary>
    /// Resolves <paramref name="member"/>'s source location. Properties (and auto-property backing
    /// fields) resolve exactly via their accessor's sequence points, since fields, properties, and
    /// types otherwise carry no sequence points of their own; anything else falls back to an
    /// approximate location on the containing type (see <see cref="SourceLocation.IsApproximate"/>
    /// and <see cref="SourceLocation.IsFileApproximate"/>).
    /// </summary>
    public static SourceLocation? ResolveSourceLocation(IMemberDefinition member, string sourceRoot)
    {
        if (member is MethodDefinition { HasBody: true } method)
        {
            var sequencePoint = FirstVisibleSequencePoint(method.DebugInformation?.SequencePoints);
            return sequencePoint != null ? FormatLocation(sequencePoint, sourceRoot, isApproximate: false) : null;
        }

        if (member is PropertyDefinition property)
        {
            var propertyLocation = ResolvePropertyLocation(property, sourceRoot);
            if (propertyLocation != null) return propertyLocation;
        }

        if (member is FieldDefinition field && TryFindAutoPropertyBackingField(field, out var owningProperty))
        {
            var propertyLocation = ResolvePropertyLocation(owningProperty, sourceRoot);
            if (propertyLocation != null) return propertyLocation;
        }

        // No sequence points on the member itself (e.g. a type, plain field, or a property with
        // no resolvable accessor) - fall back to the file of any method nearby, as an approximation.
        // Its line number would be arbitrary (just whichever method happens to be first), so it's
        // deliberately left out - only the file is reported.
        var containingType = member as TypeDefinition ?? member.DeclaringType;

        var ownSequencePoint = FindSequencePointOnTypeOrAncestors(containingType);
        if (ownSequencePoint != null) return FormatApproximateLocation(ownSequencePoint, sourceRoot, isFileApproximate: false);

        var siblingSequencePoint = FindSequencePointOnNamespaceSibling(containingType);
        return siblingSequencePoint != null ? FormatApproximateLocation(siblingSequencePoint, sourceRoot, isFileApproximate: true) : null;
    }

    /// <summary>
    /// Finds a sequence point in <paramref name="containingType"/>'s own methods, or (for a type
    /// nested in one with methods, e.g. an enum nested in the class that uses it) its declaring
    /// types. The file this resolves to is trustworthy - it's the member's own file, even though
    /// the line isn't.
    /// </summary>
    private static SequencePoint? FindSequencePointOnTypeOrAncestors(TypeDefinition? containingType)
    {
        for (var type = containingType; type != null; type = type.DeclaringType)
        {
            var sequencePoint = FirstSequencePointOnType(type);
            if (sequencePoint != null) return sequencePoint;
        }

        return null;
    }

    /// <summary>
    /// Falls back to any other type in the same namespace and module - types with no methods of
    /// their own anywhere in their declaring-type chain (enums, chiefly: they compile down to only
    /// fields) otherwise never resolve to any location at all. Unlike
    /// <see cref="FindSequencePointOnTypeOrAncestors"/>, the result here has no real relationship
    /// to the member beyond sharing a namespace, so even its file is only a guess - a namespace
    /// commonly spans multiple source files.
    /// </summary>
    private static SequencePoint? FindSequencePointOnNamespaceSibling(TypeDefinition? containingType)
    {
        if (containingType == null) return null;

        foreach (var sibling in containingType.Module.GetTypes())
        {
            if (sibling == containingType || sibling.Namespace != containingType.Namespace) continue;

            var sequencePoint = FirstSequencePointOnType(sibling);
            if (sequencePoint != null) return sequencePoint;
        }

        return null;
    }

    private static SequencePoint? FirstSequencePointOnType(TypeDefinition type)
    {
        foreach (var candidate in type.Methods)
        {
            if (!candidate.HasBody) continue;

            var sequencePoint = FirstVisibleSequencePoint(candidate.DebugInformation?.SequencePoints);
            if (sequencePoint != null) return sequencePoint;
        }

        return null;
    }

    /// <summary>Resolves a property's location via its get/set accessor's own sequence points, if either has one.</summary>
    private static SourceLocation? ResolvePropertyLocation(PropertyDefinition property, string sourceRoot)
    {
        foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
        {
            if (accessor is not { HasBody: true }) continue;

            var sequencePoint = FirstVisibleSequencePoint(accessor.DebugInformation?.SequencePoints);
            if (sequencePoint != null) return FormatLocation(sequencePoint, sourceRoot, isApproximate: false);
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="field"/> is a compiler-generated auto-property backing field
    /// (named <c>&lt;PropertyName&gt;k__BackingField</c>), and if so, the property it backs.
    /// </summary>
    private static bool TryFindAutoPropertyBackingField(FieldDefinition field, out PropertyDefinition owningProperty)
    {
        owningProperty = null!;

        var name = field.Name;
        if (name.Length < 2 || name[0] != '<') return false;

        var closingAngle = name.IndexOf('>');
        if (closingAngle <= 1 || !name.AsSpan(closingAngle).StartsWith(">k__BackingField")) return false;

        var propertyName = name[1..closingAngle];
        foreach (var property in field.DeclaringType.Properties)
        {
            if (property.Name == propertyName)
            {
                owningProperty = property;
                return true;
            }
        }

        return false;
    }

    private static SequencePoint? FirstVisibleSequencePoint(IEnumerable<SequencePoint>? sequencePoints)
    {
        if (sequencePoints == null) return null;

        foreach (var sequencePoint in sequencePoints)
        {
            if (!sequencePoint.IsHidden) return sequencePoint;
        }

        return null;
    }

    /// <summary>
    /// Resolves the source location of <paramref name="instruction"/>, walking back through
    /// preceding instructions until one carries a visible sequence point.
    /// </summary>
    public static SourceLocation? ResolveInstructionLocation(MethodDefinition method, Instruction instruction, string sourceRoot)
    {
        var debugInfo = method.DebugInformation;
        if (debugInfo == null) return null;

        var current = instruction;
        while (current != null)
        {
            var sequencePoint = debugInfo.GetSequencePoint(current);
            if (sequencePoint != null && !sequencePoint.IsHidden)
            {
                return FormatLocation(sequencePoint, sourceRoot, isApproximate: false);
            }

            current = current.Previous;
        }

        return null;
    }

    private static SourceLocation FormatLocation(SequencePoint sequencePoint, string sourceRoot, bool isApproximate)
    {
        return FormatLocationCore(sequencePoint, sourceRoot, isApproximate, isFileApproximate: false, sequencePoint.StartLine);
    }

    /// <summary>
    /// Formats an approximate location without a line number: the sequence point's own line
    /// belongs to an unrelated method (the containing type's), so reporting it would be misleading.
    /// <paramref name="isFileApproximate"/> further marks the namespace-sibling case, where even
    /// the file is only a guess.
    /// </summary>
    private static SourceLocation FormatApproximateLocation(SequencePoint sequencePoint, string sourceRoot, bool isFileApproximate)
    {
        return FormatLocationCore(sequencePoint, sourceRoot, isApproximate: true, isFileApproximate, line: null);
    }

    private static SourceLocation FormatLocationCore(SequencePoint sequencePoint, string sourceRoot, bool isApproximate, bool isFileApproximate, int? line)
    {
        var rawPath = sequencePoint.Document.Url;

        // Match on a folder boundary, not a bare prefix. Normalize separators to handle
        // PDBs or sourceRoot arguments from another OS (e.g. Windows backslashes on Unix).
        var normalizedRootPath = sourceRoot.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalizedRootPath)) + Path.DirectorySeparatorChar;
        var normalizedPath = rawPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        var path = rawPath;
        if (normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            path = Path.GetRelativePath(normalizedRootPath, normalizedPath);
        }

        return new SourceLocation(path, line, isApproximate, isFileApproximate);
    }
}
