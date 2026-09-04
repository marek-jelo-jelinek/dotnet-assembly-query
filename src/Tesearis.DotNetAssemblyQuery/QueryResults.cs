using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>A matched member and its resolved source location, if any.</summary>
public sealed record MemberDefinitionLocation(IMemberDefinition Member, SourceLocation? Location);

/// <summary>
/// A location that references a queried symbol. <see cref="Site"/> is the containing method for
/// an IL reference, or the type/member whose signature mentions the symbol for a type-position
/// usage (<see cref="IsTypePositionUsage"/>).
/// </summary>
public sealed record ReferenceSite(IMemberDefinition Site, string Kind, SourceLocation? Location, bool IsTypePositionUsage);

/// <summary>A loaded assembly, as reported by <see cref="AssemblyQuery.ListAssemblies"/>.</summary>
public sealed record AssemblyInfo(string Name, string Version, string FilePath);
