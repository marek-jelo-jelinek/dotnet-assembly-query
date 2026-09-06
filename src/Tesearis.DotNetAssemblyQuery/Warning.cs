namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// A problem or note reported alongside a result. <see cref="VerboseOnly"/> marks a warning that's
/// expected and often high-volume, so callers can hide it by default while still surfacing genuine
/// problems unconditionally.
/// </summary>
public readonly record struct Warning(string Message, bool VerboseOnly);
