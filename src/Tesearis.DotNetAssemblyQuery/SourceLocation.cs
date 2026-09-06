namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// A source location resolved from a PDB sequence point. <see cref="IsApproximate"/> is true when
/// resolved from a method other than the member itself, in which case <see cref="Line"/> is null
/// - the type's/field's own line isn't known, only its file. <see cref="IsFileApproximate"/> is
/// true for the rarer case where even the file is a guess: the member's type (and its declaring
/// types) have no methods of their own (e.g. an enum), so the location came from an unrelated
/// type that merely shares its namespace.
/// </summary>
public sealed record SourceLocation(string Path, int? Line, bool IsApproximate, bool IsFileApproximate)
{
    /// <summary>
    /// Renders as <c>path:line</c>, or just <c>path</c> when the line isn't known, flagging it
    /// when the file itself is only a namespace-sibling guess.
    /// </summary>
    public override string ToString()
    {
        var path = IsFileApproximate ? $"{Path} (approximate file)" : Path;
        return Line != null ? $"{path}:{Line}" : path;
    }
}