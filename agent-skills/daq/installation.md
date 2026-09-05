# Installing daq

## Check first

```
daq --version
```

If that works, skip the rest of this file.

## Install

`daq` ships as the `dotnet-assembly-query` NuGet package (a .NET tool), with `daq` as its
command name.

```
dotnet tool install --global dotnet-assembly-query
```

Requires the .NET SDK (to install the tool) and a .NET 8 or .NET 10 runtime on the machine
(the tool targets both `net8.0` and `net10.0` and rolls forward to the latest installed major).

Prefer a repo-local install (no admin/global state, works in CI) when that fits the project
better:

```
dotnet new tool-manifest   # only if the repo doesn't already have one
dotnet tool install dotnet-assembly-query
dotnet tool run daq -- --version
```

A local install's `daq` isn't on `PATH` directly - invoke it via `dotnet daq` or `dotnet tool
run daq` (see the tool-manifest docs if that's unfamiliar).

## PATH issues after a global install

A global install puts the `daq` shim in `~/.dotnet/tools` (`%USERPROFILE%\.dotnet\tools` on
Windows). If `daq --version` reports "command not found" right after installing, that
directory usually isn't on `PATH` yet - open a new shell, or add it manually.

## Avoiding a permission prompt per call

If you're an AI agent (e.g. Claude Code) running `daq` via a shell tool, ask the user to add
an allowlist entry so it doesn't prompt on every call. For Claude Code, in
`.claude/settings.json`:

```json
{
  "permissions": {
    "allow": ["Bash(daq:*)"]
  }
}
```
