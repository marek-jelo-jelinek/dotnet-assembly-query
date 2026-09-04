namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// A source location resolved from a PDB sequence point. <see cref="IsApproximate"/> is true when
/// resolved from the containing type's first method rather than the member itself.
/// </summary>
public sealed record SourceLocation(string Path, int Line, bool IsApproximate)
{
    /// <summary>Renders as <c>path:line</c>, noting when the location is approximate.</summary>
    public override string ToString()
    {
        return IsApproximate ? $"{Path}:{Line} (approximate - containing type's first method)" : $"{Path}:{Line}";
    }
}