# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.0.0] - 2026-09-06

### Added

- `go-to-definition`: resolves properties/auto-properties via accessor sequence points;
  approximate fallback for types, fields, enum members.
- `--json`: `IsFileApproximate` flags best-effort file guesses.
- `agent-skills/daq/`: Claude Code skill/plugin.
- `implementations --framework-path`: resolves via extra assemblies; auto-discovers local shared
  framework.
- `implementations --include-framework-results`: opt into framework-only implementers.
- `hover` on `[DllImport]`: shows native module/entry point.
- `find-symbol --contains`: substring match (replaces `search`).
- `--verbose`: shows diagnostic warnings hidden by default.

### Changed

- **Breaking:** CLI assembly renamed to `daq` (was `Tesearis.DotNetAssemblyQuery.Cli`). Published
  standalone binaries are now named `daq`/`daq.exe`.
- **Breaking:** `implementations`: distinguishes "not indexed" from "no implementers found"
  (plain-text and `--json`); `--json` now returns an object (`TargetIndexed`, `Hint`,
  `Implementations`) instead of a bare array.
- **Breaking:** `--framework-path` no longer reports framework-only implementers by default.
- **Breaking:** `--assembly`/`--dir` merged into repeatable `--path`.
- Native DLLs from `--path` directory scans are skipped, with a summary warning (`--verbose` to
  see it).
- `go-to-definition`/`find-references`: "no source location" message now distinguishes "no usable
  PDB" from "member has no sequence points" instead of one generic message.
- `DaemonRequest`: added `FrameworkPaths`, `IncludeFrameworkResults`, `Contains` fields.
- Wire protocol bumped to 2; old daemons now self-terminate instead of ignoring new fields.

### Removed

- **Breaking:** `search` removed; use `find-symbol --contains`.

### Fixed

- `--help` usage line showed the long assembly name instead of `daq`.
- framework auto-discovery: a `DOTNET_ROOT` pointing at a directory without a `shared/`
  folder was mistaken for a valid runtime root.

## [1.0.0] - 2026-09-05

### Added

- Initial implementation: `daq` CLI (`find-symbol`, `search`, `hover`, `go-to-definition`, `find-references`,
  `list-members`, `implementations`, `list-assemblies`, `daemon status|stop|start`) with plain-text and `--json`
  output, the `Tesearis.DotNetAssemblyQuery` library for reading assemblies and portable PDBs directly via
  Mono.Cecil (no build/MSBuild/workspace dependency), and a warm-cache background daemon (named-pipe IPC) for
  faster repeated queries.