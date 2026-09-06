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
    /// approximate location on the containing type.
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
        // no resolvable accessor) - fall back to the first visible sequence point of any method on
        // the containing type, as an approximation.
        var containingType = member as TypeDefinition ?? member.DeclaringType;
        SequencePoint? anySequencePoint = null;
        if (containingType != null)
        {
            foreach (var candidate in containingType.Methods)
            {
                if (!candidate.HasBody) continue;

                anySequencePoint = FirstVisibleSequencePoint(candidate.DebugInformation?.SequencePoints);
                if (anySequencePoint != null) break;
            }
        }

        return anySequencePoint != null ? FormatLocation(anySequencePoint, sourceRoot, isApproximate: true) : null;
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

        return new SourceLocation(path, sequencePoint.StartLine, isApproximate);
    }
}
