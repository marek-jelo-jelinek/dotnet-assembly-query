# Tesearis.DotNetAssemblyQuery

A tiny, fast CLI that answers "where is this symbol / what calls it" by reading compiled
.NET assemblies and their PDBs: no build, no workspace, no decompiler.

Symbol lookup, hover info, go-to-definition and find-references over one or more compiled
.NET assemblies (with portable PDBs), resolving straight back to real source `file:line`
via debug sequence points. Built for AI coding agents and scripting: instant startup,
plain-text output, zero project-system dependency.

## Features

- `find-symbol <name>` - list every type/method/field/property matching a name.
- `search <term>` - list every type/method/field/property whose name contains `<term>`
  (case-insensitive substring match), for discovery when you only know part of a name.
- `hover <name>` - print each match's signature; overloaded methods are listed with their
  distinguishing parameter list.
- `go-to-definition <name>` - resolve each match to a `file:line` via PDB sequence points.
- `find-references <name>` - find IL-level usages (calls, casts, `typeof`, field
  loads/stores) plus type-position usages (base types, interfaces, field/parameter/return
  types).
- `list-members <type>` - list a type's methods, fields, properties, and nested types.
- `implementations <interface-or-base-type>` - find every type that implements an interface
  or derives from a base type, transitively (interfaces implemented via a base class, and
  interfaces that extend other interfaces, both count).
- `list-assemblies` - list the loaded assemblies (name, version, file path).
- Assembly discovery via repeatable `--assembly <path>` (accepts a glob) and `--dir <path>`
  (scanned non-recursively for `*.dll`), falling back to the current directory when neither
  is given.
- Explicit `--source-root <path>` for relativizing resolved source paths (defaults to the
  current directory).
- Match filters: `--kind type|method|field|property` restricts matches to that member kind
  (on `list-members`, it restricts which kind of member is *listed* instead); `--namespace <ns>`
  and `--assembly-name <name>` restrict matches to a containing namespace/assembly (exact match).
  All three are optional and combine with each other and the name match. `implementations` takes
  `--namespace`/`--assembly-name` (to disambiguate the interface/base type) but not `--kind`;
  `list-assemblies` takes neither.

## Packages

This repo ships as two NuGet packages:

- **[`dotnet-assembly-query`](https://www.nuget.org/packages/dotnet-assembly-query)** - the
  `daq` global/local `dotnet tool` (CLI). This is what most people want.
- **`Tesearis.DotNetAssemblyQuery`** - the underlying library (symbol lookup, hover,
  go-to-definition, find-references), for tools that want to query assemblies in-process instead
  of shelling out to `daq` and parsing stdout - e.g. a future editor plugin or MCP server.
  Its query methods (`AssemblyQuery`, `AssemblyLoading`) return plain data (`IMemberDefinition`s,
  `SourceLocation`s, etc.), not console output or exit codes.

## Usage

```
daq <find-symbol|search|hover|go-to-definition|find-references|list-members> <name> \
  [--assembly <path>]... [--dir <path>]... [--source-root <path>] \
  [--kind type|method|field|property] [--namespace <ns>] [--assembly-name <name>] \
  [--no-daemon] [--daemon-idle-timeout <seconds>] [--json]

daq implementations <interface-or-base-type> \
  [--assembly <path>]... [--dir <path>]... [--namespace <ns>] [--assembly-name <name>] \
  [--no-daemon] [--daemon-idle-timeout <seconds>] [--json]

daq list-assemblies \
  [--assembly <path>]... [--dir <path>]... [--no-daemon] [--daemon-idle-timeout <seconds>] [--json]
```

```
daq find-symbol MyClass --dir ./bin/Debug/net8.0
daq search Repo --dir ./bin/Debug/net8.0
daq hover DoWork --dir ./bin/Debug/net8.0
daq go-to-definition MyClass --dir ./bin/Debug/net8.0 --source-root .
daq find-references DoWork --dir ./bin/Debug/net8.0 --source-root .
daq find-symbol Value --kind property --namespace MyApp.Models --dir ./bin/Debug/net8.0
daq find-symbol MyClass --dir ./bin/Debug/net8.0 --json
daq list-members MyClass --dir ./bin/Debug/net8.0
daq implementations IMyInterface --dir ./bin/Debug/net8.0
daq list-assemblies --dir ./bin/Debug/net8.0
```

Pass `--json` to any command for machine-readable output: each command prints one JSON
array (a single object for `daemon start`) to stdout instead of text, and exits 0 on a
successful run even with zero matches (an empty array, not an error). Errors and warnings
are unaffected by `--json` - they're always plain text on stderr with the usual exit codes,
never JSON.

Every flag also accepts `--flag=value` in addition to `--flag value`.

Exit codes: `0` on success (including a query that found nothing - that's a valid result, not
an error), `2` on a command-line usage error (bad/unknown flag, missing value, unknown command),
`1` on any other runtime failure (e.g. an assembly failed to load).

### Background daemon

Repeated calls against the same, unchanged DLL set (e.g. an AI agent driving `daq` many times
in a row) are slow mainly because every invocation reloads every DLL/PDB from scratch. To speed
that up, `daq` transparently starts a background daemon the first time it's asked about a given
DLL set: the first call runs exactly as before (no slower, no different output), and as a side
effect spawns a daemon that keeps those modules loaded in memory. Every later call against the
same DLL set is answered by that daemon instead of reloading anything - the daemon re-checks
each file's size/timestamp before answering, so a rebuild is always picked up, never served
stale. It shuts itself down after 30 minutes of inactivity (override with
`--daemon-idle-timeout <seconds>` or the `DAQ_DAEMON_IDLE_TIMEOUT_SECONDS` env var).

```
daq daemon status [--json]                                  # list running daemons
daq daemon stop [--assembly <path>]... [--dir <path>]... [--json]   # stop one (or all, with no args)
daq daemon start --foreground --dir ./bin/Debug/net8.0 [--json]     # run one in the foreground
```

Pass `--no-daemon` to any command to disable the mechanism entirely (no connect attempt, no
spawn) - useful in CI or sandboxed environments.

### As a library

```csharp
using Tesearis.DotNetAssemblyQuery;

var dllPaths = AssemblyLoading.DiscoverDllPaths(assemblyPaths: [], directories: ["./bin/Debug/net8.0"], out _);
var modules = AssemblyLoading.LoadModules(dllPaths, out _);
var allTypes = modules.SelectMany(m => m.GetTypes()).ToList();

foreach (var site in AssemblyQuery.FindReferences(modules, allTypes, "DoWork", sourceRoot: "."))
{
    Console.WriteLine($"{site.Site.FullName} ({site.Kind}) -> {site.Location}");
}
```

## Building & testing

```
dotnet build Tesearis.DotNetAssemblyQuery.slnx
dotnet test Tesearis.DotNetAssemblyQuery.slnx
```

## License

MIT - see [LICENSE](LICENSE).
