# Troubleshooting

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success - **including a query that matched nothing.** An empty result is a valid answer, not a failure; don't retry it as if it errored. |
| `1` | Runtime failure: an assembly failed to load, no DLLs were found, or some other exception during the query. Message goes to stderr. |
| `2` | Command-line usage error: unknown command, unknown/malformed flag, or a flag that failed validation. |

## Common messages

- `No assemblies found (use --assembly/--dir, or run from a directory containing .dll files).` (exit 1) - none of `--assembly`/`--dir` resolved to any
  DLL, and the current directory has none either. Point `daq` at a real build output directory (`dotnet build` first if it doesn't exist yet).
- `Error: <message>` (exit 1) - an assembly or PDB failed to load, or another runtime exception was thrown. The message is the underlying exception's,
  printed as-is.
- `Unknown command: <name>` (exit 2) - shouldn't normally happen (the CLI parser already rejects unknown subcommands before dispatch), but is the
  fallback if it ever does.
- `--kind expects one of 'type', 'method', 'field', 'property', got '<value>'.` (exit 2)
- `--daemon-idle-timeout expects a positive number of seconds, got '<value>'.` (exit 2)
- `Type '<name>' was not found in the indexed assemblies. If it's a framework/BCL type (e.g.
  IDisposable), index its defining assembly too (e.g. add System.Private.CoreLib.dll via
  --assembly or --dir), or retry with --framework-dir.` - printed by `implementations` (exit 0, this is a valid outcome, not an error) when the
  interface/base type itself isn't among the indexed types. This is deliberately distinct from `No implementations of '<name>' found.`, which means
  the type *is* indexed but nothing implements/derives from it.
- `skipped N native (non-.NET) DLL(s) found via --dir scan` (stderr warning, doesn't affect exit code) - a `--dir` scan silently excludes non-managed
  DLLs it finds alongside real assemblies (native AOT shims, SQLite, SkiaSharp, etc.) instead of failing to load each one. A DLL passed explicitly via
  `--assembly` is never filtered this way - if it isn't managed, loading it fails for real and is reported per-file.

## Known limitations

- `implementations` only searches types physically declared in an indexed assembly - including the interface/base type itself. A framework/BCL
  interface (e.g. `IDisposable`) requires indexing its defining assembly (`System.Private.CoreLib.dll`) explicitly, or passing a bare
  `--framework-dir` so `daq` discovers and loads the local machine's matching `Microsoft.NETCore.App` shared framework on demand. For an
  interface/base type that isn't part of any dotnet SDK's shared framework, pass `--framework-dir <path>` (repeatable, mixing directories and explicit
  file paths) to point `daq` at those locations directly instead of relying on auto-discovery.
- `go-to-definition`/`find-references` resolve source locations from portable PDB sequence points. An assembly built without a portable PDB (or with
  an old-style Windows PDB, or without one at all) resolves to "no source location available" for its members - that's not a bug, there's nothing to
  resolve.
- The background daemon's staleness check is timestamp/size-based, not content-based (see `daemon.md`) - a touch with no real content change can
  trigger one extra reload.

## Diagnosing daemon spawn issues

`daq` swallows a failed daemon-spawn attempt silently by default (a query's result never depends on whether the daemon spawn succeeded). Set
`DAQ_DEBUG=1` in the environment to print the warning instead. `daq daemon status` shows what's actually running.
