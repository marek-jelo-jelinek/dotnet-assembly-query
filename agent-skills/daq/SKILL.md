---
name: daq
description: Navigate compiled .NET assemblies (find/hover/go-to-definition/find-references/list-members/implementations) using the daq CLI instead of grep or manual source reading. Use whenever working in a .NET repo or build output and asked where a symbol is defined, what calls it, what implements an interface, or what a type's members are.
---

# daq

`daq` reads compiled .NET assemblies and portable PDBs directly (via Mono.Cecil) - no build system, no decompiler - and resolves symbols to real
`file:line` locations in milliseconds. Prefer it over grepping or scanning files by hand for a symbol's identity, usage, or type hierarchy. Fall back
to grep for non-symbol text (comments, string literals, config files, non-.NET code).

## Pointing it at the build output

Every query command takes a repeatable `--path <path>` that auto-detects each entry: a
directory is scanned non-recursively for `*.dll`, anything else is resolved as an exact file or
glob pattern; with no `--path`, it falls back to the current directory. `--source-root <path>`
controls how resolved source paths are relativized (default: cwd).

```
daq find-symbol MyClass --path ./bin/Debug/net8.0
```

## Command cheatsheet

| Question | Command |
|---|---|
| Where is `X` defined? | `daq go-to-definition X` |
| What calls / uses `X`? | `daq find-references X` |
| What is `X`'s signature? Is it overloaded? | `daq hover X` |
| I only know part of the name | `daq find-symbol <term> --contains` |
| What members does type `T` have? | `daq list-members T` |
| What implements/derives from `I` (transitively)? | `daq implementations I` |
| What assemblies are loaded? | `daq list-assemblies` |
| I have the exact name but don't know its kind/namespace/assembly | `daq find-symbol X` |

Narrow ambiguous matches (any query command except `list-assemblies`; `implementations` skips
`--kind`): `--kind type|method|field|property`, `--namespace <ns>`, `--assembly-name <name>`.

`implementations --framework-path` (repeatable): if the target interface/base type isn't indexed, resolves it from extra types. Bare
(`--framework-path` with no value) discovers and loads the local machine's matching `Microsoft.NETCore.App` shared framework instead of
requiring you to index it manually. Given one or more values, each is a directory or an explicit file path - mix freely. By default,
`--framework-path` assemblies are reference-only: they help resolve the target type, but types declared only there aren't reported as
implementers (so results stay limited to your own `--path` types instead of every matching BCL type). Pass
`--include-framework-results` to include framework-declared implementers too.

## Background daemon

By default, the first query against a given `--path` set spawns a background daemon that later queries against the same set reuse instead
of reloading everything. Pass `--no-daemon` to skip it for a call (e.g. in CI). See `daemon.md` for details and the `daemon status|stop|start`
commands.

## Output

Add `--json` for one JSON array per command; errors and warnings stay on stderr either way. Exit `0` on success - **including a zero-match query;
that's a valid result, not an error** - `1` on a runtime failure, `2` on a usage error. See `troubleshooting.md` for the full breakdown and known
limitations.

## More info

- `installation.md` - install steps, avoiding permission prompts.
- `troubleshooting.md` - known limitations, exit codes, common errors.
- `daemon.md` - background daemon behavior and `daemon start|stop|status`.
