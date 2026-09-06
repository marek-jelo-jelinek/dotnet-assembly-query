using System.Text.Json.Serialization;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>The <c>--json</c> shape of a resolved <see cref="SourceLocation"/>.</summary>
internal sealed record SourceLocationJson(string Path, int Line, bool IsApproximate);

internal sealed record FindSymbolResultJson(string Kind, string Name, string Assembly);

internal sealed record HoverResultJson(string Signature);

/// <summary><see cref="UnavailableReason"/> is set iff <see cref="Location"/> is null.</summary>
internal sealed record GoToDefinitionResultJson(string Kind, string QualifiedName, string Assembly, SourceLocationJson? Location, string? UnavailableReason);

internal sealed record FindReferenceResultJson(string SiteFullName, string ReferenceKind, bool IsTypePositionUsage, SourceLocationJson? Location);

internal sealed record ListMembersResultJson(string Kind, string Name, string Assembly);

internal sealed record ImplementationsResultJson(string Kind, string Name, string Assembly);

internal sealed record ListAssembliesResultJson(string Name, string Version, string FilePath);

/// <summary>
/// Source-generated JSON context for <c>--json</c> command output. Kept separate from
/// <see cref="DaemonJsonContext"/>, which is scoped to the daemon's pipe wire protocol and its
/// own <c>daemon status|stop|start</c> output DTOs.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<FindSymbolResultJson>))]
[JsonSerializable(typeof(List<HoverResultJson>))]
[JsonSerializable(typeof(List<GoToDefinitionResultJson>))]
[JsonSerializable(typeof(List<FindReferenceResultJson>))]
[JsonSerializable(typeof(List<ListMembersResultJson>))]
[JsonSerializable(typeof(List<ImplementationsResultJson>))]
[JsonSerializable(typeof(List<ListAssembliesResultJson>))]
internal partial class CliOutputJsonContext : JsonSerializerContext;
