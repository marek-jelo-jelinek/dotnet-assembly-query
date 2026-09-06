namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// A source location resolved from a PDB sequence point. <see cref="IsApproximate"/> is true when
/// resolved from the containing type's first method rather than the member itself, in which case
/// <see cref="Line"/> is null - the type's/field's own line isn't known, only its file.
/// </summary>
public sealed record SourceLocation(string Path, int? Line, bool IsApproximate)
{
    /// <summary>Renders as <c>path:line</c>, or just <c>path</c> when the line isn't known.</summary>
    public override string ToString()
    {
        return Line != null ? $"{Path}:{Line}" : Path;
    }
}