# Tesearis.DotNetAssemblyQuery

A tiny, fast CLI that answers "where is this symbol / what calls it" by reading compiled
.NET assemblies and their PDBs: no build, no workspace, no decompiler.

Symbol lookup, hover info, go-to-definition and find-references over one or more compiled
.NET assemblies (with portable PDBs), resolving straight back to real source `file:line`
via debug sequence points. Built for AI coding agents and scripting: instant startup,
plain-text output, zero project-system dependency.

## Features

- `find-symbol <name>` - list every type/method/field/property matching a name. Pass
  `--contains` to match names containing `<name>` (case-insensitive substring match) instead
  of requiring an exact match, for discovery when you only know part of a name.
- `hover <name>` - print each match's signature; overloaded methods are listed with their
  distinguishing parameter list. A `[DllImport]` (P/Invoke) method's signature also shows the
  native module and entry point it targets, e.g. `// P/Invoke: user32.dll!MessageBoxW`.
- `go-to-definition <name>` - resolve each match to a `file:line` via PDB sequence points. A
  type/field/enum with no sequence points of its own resolves approximately (its own file,
  no line) via a nearby method; if the type has no methods anywhere in its own declaring-type
  chain either (e.g. a top-level enum), it falls back further to any type sharing its namespace
  - in that rarer case even the file is a guess, flagged as `IsFileApproximate` in `--json`.
- `find-references <name>` - find IL-level usages (calls, casts, `typeof`, field
  loads/stores) plus type-position usages (base types, interfaces, field/parameter/return
  types).
- `list-members <type>` - list a type's methods, fields, properties, and nested types.
- `implementations <interface-or-base-type>` - find every type that implements an interface
  or derives from a base type, transitively (via a base class, or via interface-extends-interface,
  both count). Only types declared in an indexed assembly are searchable, including the target
  itself: a framework/BCL type like `IDisposable` needs its defining assembly indexed (e.g.
  `System.Private.CoreLib.dll`), or pass `--framework-path` (repeatable) to resolve it on demand
  instead - bare, it auto-discovers the local machine's matching `Microsoft.NETCore.App` shared
  framework; given one or more values, each is a directory (scanned like `--path`) or an explicit
  file path, mixed freely (e.g. for a Unity Editor install with reference assemblies scattered
  across several directories). By default `--framework-path` assemblies are reference-only: they
  help resolve the target and let ancestry walks cross into a framework base type, but their own
  types aren't reported as implementers (avoids flooding results with every BCL type that
  implements a common interface). Pass `--include-framework-results` to include those too. A
  target that's indexed but has no implementers is reported separately from one that isn't
  indexed at all, instead of a misleading "no implementations found" for both cases.
- `list-assemblies` - list the loaded assemblies (name, version, file path).
- Assembly discovery via repeatable `--path <path>`, auto-detected per entry: a directory is
  scanned non-recursively for `*.dll`; anything else resolves as an exact file or glob pattern.
  Falls back to the current directory when no `--path` is given. A directory scan silently skips
  native (non-.NET) DLLs it finds alongside managed ones (native AOT shims, SQLite, SkiaSharp,
  etc.), reporting one collapsed `skipped N native (non-.NET) DLL(s) found via --path directory
  scan` warning (hidden by default, expected and not actionable; pass `--verbose` to see it)
  instead of failing on each. A DLL named explicitly is never filtered this way - if it isn't
  managed, its load failure is still reported for real.
- Explicit `--source-root <path>` for relativizing resolved source paths (defaults to the
  current directory).
- Match filters: `--kind type|method|field|property` restricts matches to that member kind
  (on `list-members`, it restricts which kind of member is *listed* instead); `--namespace <ns>`
  and `--assembly-name <name>` restrict matches to a containing namespace/assembly (exact match).
  All three are optional and combine with each other and the name match. `implementations` takes
  `--namespace`/`--assembly-name` (to disambiguate the interface/base type) but not `--kind`;
  `list-assemblies` takes neither.

## Packages

This repo ships as three NuGet packages:

- **[`dotnet-assembly-query`](https://www.nuget.org/packages/dotnet-assembly-query)** - the
  `daq` global/local `dotnet tool` (CLI). This is what most people want.
- **`Tesearis.DotNetAssemblyQuery`** - the underlying library (symbol lookup, hover,
  go-to-definition, find-references), for tools that want to query assemblies in-process instead
  of shelling out to `daq` and parsing stdout - e.g. a future editor plugin or MCP server.
  Its query methods (`AssemblyQuery`, `AssemblyLoading`) return plain data (`IMemberDefinition`s,
  `SourceLocation`s, etc.), not console output or exit codes.
- **`Tesearis.DotNetAssemblyQuery.Daemon`** - the warm-cache background daemon behind the CLI,
  factored out so other front ends can host it against their own already-loaded Mono.Cecil
  modules without reimplementing the pipe/process subsystem.

## Usage

```
daq <find-symbol|hover|go-to-definition|find-references|list-members> <name> \
  [--path <path>]... [--source-root <path>] \
  [--kind type|method|field|property] [--namespace <ns>] [--assembly-name <name>] \
  [--no-daemon] [--daemon-idle-timeout <seconds>] [--json] [--verbose]

daq find-symbol <name> --contains \
  [--path <path>]... [--source-root <path>] \
  [--kind type|method|field|property] [--namespace <ns>] [--assembly-name <name>] \
  [--no-daemon] [--daemon-idle-timeout <seconds>] [--json] [--verbose]

daq implementations <interface-or-base-type> \
  [--path <path>]... [--source-root <path>] [--namespace <ns>] \
  [--assembly-name <name>] [--framework-path [<path>...]] [--include-framework-results] \
  [--no-daemon] [--daemon-idle-timeout <seconds>] [--json] [--verbose]

daq list-assemblies \
  [--path <path>]... [--source-root <path>] [--no-daemon] \
  [--daemon-idle-timeout <seconds>] [--json] [--verbose]
```

```
daq find-symbol MyClass --path ./bin/Debug/net8.0
daq find-symbol Repo --contains --path ./bin/Debug/net8.0
daq hover DoWork --path ./bin/Debug/net8.0
daq go-to-definition MyClass --path ./bin/Debug/net8.0 --source-root .
daq find-references DoWork --path ./bin/Debug/net8.0 --source-root .
daq find-symbol Value --kind property --namespace MyApp.Models --path ./bin/Debug/net8.0
daq find-symbol MyClass --path ./bin/Debug/net8.0 --json
daq list-members MyClass --path ./bin/Debug/net8.0
daq implementations IMyInterface --path ./bin/Debug/net8.0
daq implementations IDisposable --path ./bin/Debug/net8.0 --framework-path
daq implementations IDisposable --path ./bin/Debug/net8.0 --framework-path --include-framework-results
daq implementations IUpdate --path ./Library/ScriptAssemblies --framework-path /path/to/custom/framework
daq implementations IUpdate --path ./Library/ScriptAssemblies \
  --framework-path /path/to/custom/framework \
  --framework-path /path/to/custom/module.dll
daq list-assemblies --path ./bin/Debug/net8.0
```

Pass `--json` to any command for machine-readable output: each command prints one JSON
array (a single object for `daemon start`) to stdout instead of text, and exits 0 on a
successful run even with zero matches (an empty array, not an error). Errors and warnings
are unaffected by `--json` - they're always plain text on stderr with the usual exit codes,
never JSON.

`implementations --json` is the other single-object exception: `{"targetIndexed": bool,
"hint": string|null, "implementations": [...]}`. `targetIndexed` is `false` (with `hint`
explaining why, and `implementations` empty) when the target type itself isn't indexed at
all, as distinct from `targetIndexed: true` with an empty `implementations` array, which
means the type was found but genuinely has no reported implementers.

Pass `--verbose` to any command to also show diagnostic warnings that are hidden by default,
e.g. the native (non-.NET) DLL skip note described above.

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
daq daemon stop [--path <path>]... [--json]                 # stop one (or all, with no args)
daq daemon start --foreground --path ./bin/Debug/net8.0 [--json] [--verbose]   # run one in the foreground
```

Pass `--no-daemon` to any command to disable the mechanism entirely (no connect attempt, no
spawn) - useful in CI or sandboxed environments.

### Agent integration

Add an allowlist entry so an agent (e.g. Claude Code) can call this without a permission
prompt each time, in `.claude/settings.json`:

```json
{
  "permissions": {
    "allow": ["Bash(daq:*)"]
  }
}
```

For Claude Code specifically, this repo ships a `daq` skill as an installable plugin. From
your project, run:

```
/plugin marketplace add marek-jelo-jelinek/dotnet-assembly-query
/plugin install daq@marek-jelo-jelinek
```

It documents preflight, the full command/flag cheatsheet, and the allowlist snippet above, so
the agent picks up `daq` correctly without re-deriving usage from this README each time.

### As a library

```csharp
using Tesearis.DotNetAssemblyQuery;

var dllPaths = AssemblyLoading.DiscoverDllPaths(["./bin/Debug/net8.0"], out _);
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
