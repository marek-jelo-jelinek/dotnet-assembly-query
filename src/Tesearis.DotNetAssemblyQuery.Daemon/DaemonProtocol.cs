using Mono.Cecil;
using System.Text.Json.Serialization;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// The current wire protocol version. Sent as a raw 4-byte handshake before any JSON frame - a
/// daemon that sees a mismatched version closes the connection and self-terminates (see
/// <see cref="DaemonHost"/>) rather than risk misinterpreting a request/response shape it
/// wasn't built for, letting the newer client's normal "connect failed -&gt; spawn fresh daemon"
/// path take over unmodified.
/// </summary>
public static class DaemonProtocolVersion
{
    /// <summary>The current protocol version number.</summary>
    public const int Current = 1;
}

/// <summary>A query request sent from a <see cref="DaemonClient"/> to a warm <see cref="DaemonHost"/>.</summary>
public sealed record DaemonRequest(string Command, string Name, string SourceRoot, List<string> DllPaths, string? Kind = null, string? Namespace = null, string? AssemblyName = null, bool Json = false, List<string>? FrameworkPaths = null, bool IncludeFrameworkResults = false, bool Contains = false);

/// <summary>The full buffered result of dispatching a <see cref="DaemonRequest"/>.</summary>
public sealed record DaemonResponse(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs one <see cref="DaemonRequest"/> against already-loaded modules/types, writing its result
/// via <see cref="Console.Out"/>/<see cref="Console.Error"/> and returning the exit code -
/// <see cref="DaemonHost"/> captures that output and ships it back over the pipe. Each host
/// project (the CLI, or another front end embedding this daemon) supplies its own dispatch,
/// typically by adapting <see cref="DaemonRequest"/>'s fields into that project's own options
/// type. <paramref name="autoFrameworkTypes"/> is <see cref="DaemonHost"/>'s lazily-populated,
/// process-lifetime-cached extra-types provider for <see cref="DaemonRequest.FrameworkPaths"/> -
/// see <see cref="DaemonHost"/>.
/// </summary>
public delegate int DaemonDispatch(DaemonRequest request, List<ModuleDefinition> modules, List<TypeDefinition> allTypes, Func<List<TypeDefinition>>? autoFrameworkTypes);

internal sealed record DaemonStatusEntryJson(string Signature, int ProcessId, int DllCount, string Uptime);

internal sealed record DaemonStopResultJson(string Signature, int ProcessId, bool StoppedGracefully);

/// <summary><see cref="Status"/> is <c>"started"</c> or <c>"already-running"</c>.</summary>
internal sealed record DaemonStartResultJson(string Status, string? Signature);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(DaemonRequest))]
[JsonSerializable(typeof(DaemonResponse))]
[JsonSerializable(typeof(DaemonRegistryEntry))]
internal partial class DaemonJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated JSON context for <c>daemon status|stop|start</c> command output (as opposed
/// to <see cref="DaemonJsonContext"/>, which is scoped to the daemon's pipe wire protocol).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<DaemonStatusEntryJson>))]
[JsonSerializable(typeof(List<DaemonStopResultJson>))]
[JsonSerializable(typeof(DaemonStartResultJson))]
internal partial class DaemonControlJsonContext : JsonSerializerContext;
