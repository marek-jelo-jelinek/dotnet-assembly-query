# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres
to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-09-05

### Added

- Initial implementation: `daq` CLI (`find-symbol`, `search`, `hover`, `go-to-definition`, `find-references`,
  `list-members`, `implementations`, `list-assemblies`, `daemon status|stop|start`) with plain-text and `--json`
  output, the `Tesearis.DotNetAssemblyQuery` library for reading assemblies and portable PDBs directly via
  Mono.Cecil (no build/MSBuild/workspace dependency), and a warm-cache background daemon (named-pipe IPC) for
  faster repeated queries.