using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Resolves DLL paths from CLI arguments and loads them as Mono.Cecil modules.</summary>
public static class AssemblyLoading
{
    /// <summary>
    /// Resolves DLL paths from <c>--path</c>, falling back to the current directory when empty.
    /// Each entry is auto-detected as a directory (non-recursive <c>*.dll</c> scan) or a file/glob
    /// pattern. Unresolved patterns are reported in <paramref name="warnings"/>. A directory scan
    /// silently drops native (non-.NET) DLLs it finds and reports a single collapsed count in
    /// <paramref name="warnings"/> (flagged <see cref="Warning.VerboseOnly"/>) instead of
    /// failing to load each one individually later. DLLs named explicitly (not via a directory
    /// scan) are never filtered this way, so a direct request about a specific file still gets a
    /// real per-file load failure if it turns out not to be managed.
    /// </summary>
    public static List<string> DiscoverDllPaths(IReadOnlyList<string> paths, out List<Warning> warnings)
    {
        var resolvedPaths = new List<string>();
        warnings = [];

        var effectivePaths = paths.Count == 0 ? [Directory.GetCurrentDirectory()] : paths;

        var nativeSkippedCount = 0;
        foreach (var entry in effectivePaths)
        {
            if (Directory.Exists(entry))
            {
                try
                {
                    foreach (var dllPath in Directory.GetFiles(entry, "*.dll"))
                    {
                        if (ManagedAssemblyDetection.IsManagedAssembly(dllPath))
                        {
                            resolvedPaths.Add(dllPath);
                        }
                        else
                        {
                            nativeSkippedCount++;
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    warnings.Add(new Warning($"--path could not be read: {entry} ({ex.Message})", VerboseOnly: false));
                }

                continue;
            }

            var matches = ResolveGlob(entry);
            if (matches.Count == 0)
            {
                warnings.Add(new Warning($"--path matched no files: {entry}", VerboseOnly: false));
            }

            resolvedPaths.AddRange(matches);
        }

        if (nativeSkippedCount > 0)
        {
            warnings.Add(new Warning(
                $"skipped {nativeSkippedCount} native (non-.NET) DLL(s) found via --path directory scan",
                VerboseOnly: true));
        }

        // Normalize before dedup so the same DLL reached via two different path spellings (e.g.
        // via an explicit file and a directory scan) is only loaded once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniquePaths = new List<string>();
        foreach (var path in resolvedPaths)
        {
            if (seen.Add(Path.GetFullPath(path)))
            {
                uniquePaths.Add(path);
            }
        }

        return uniquePaths;
    }

    private static List<string> ResolveGlob(string pattern)
    {
        if (File.Exists(pattern)) return [pattern];

        if (pattern.Contains("**"))
        {
            var firstWildcard = pattern.IndexOfAny(['*', '?']);
            string baseDir;
            if (firstWildcard >= 0)
            {
                var prefix = pattern[..firstWildcard];
                var lastSlash = prefix.LastIndexOfAny(['/', '\\']);
                baseDir = lastSlash >= 0 ? prefix[..lastSlash] : ".";
                if (string.IsNullOrEmpty(baseDir)) baseDir = ".";
            }
            else
            {
                baseDir = Path.GetDirectoryName(pattern) ?? ".";
                if (string.IsNullOrEmpty(baseDir)) baseDir = ".";
            }

            if (!Directory.Exists(baseDir)) return [];

            var files = Directory.GetFiles(baseDir, "*", SearchOption.AllDirectories);
            var regex = GlobToRegex(pattern);
            var matches = new List<string>();
            foreach (var file in files)
            {
                var normalized = file.Replace('\\', '/');
                if (normalized.StartsWith("./", StringComparison.Ordinal) && !pattern.StartsWith("./", StringComparison.Ordinal))
                {
                    normalized = normalized[2..];
                }

                if (regex.IsMatch(normalized))
                {
                    matches.Add(file);
                }
            }

            return matches;
        }

        var dir = Path.GetDirectoryName(pattern);
        var filePattern = Path.GetFileName(pattern);
        var searchDir = string.IsNullOrEmpty(dir) ? "." : dir;

        return Directory.Exists(searchDir) ? [.. Directory.GetFiles(searchDir, filePattern)] : [];
    }

    private static Regex GlobToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var sb = new StringBuilder("^");
        var i = 0;
        while (i < normalized.Length)
        {
            if (i + 1 < normalized.Length && normalized[i] == '*' && normalized[i + 1] == '*')
            {
                if (i + 2 < normalized.Length && normalized[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 3;
                }
                else
                {
                    sb.Append(".*");
                    i += 2;
                }
            }
            else if (normalized[i] == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (normalized[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                var c = normalized[i];
                if (".+$^()[]{}|\\".Contains(c))
                {
                    sb.Append('\\');
                }

                sb.Append(c);
                i++;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Loads each DLL as a Mono.Cecil module, using its PDB when present, with a fresh resolver
    /// scoped only to <paramref name="dllPaths"/>' own directories. Failed loads are reported in
    /// <paramref name="warnings"/>. Callers must dispose the returned modules.
    /// </summary>
    public static List<ModuleDefinition> LoadModules(IReadOnlyList<string> dllPaths, out List<string> warnings)
    {
        return LoadModules(dllPaths, new DefaultAssemblyResolver(), out warnings);
    }

    /// <summary>
    /// Like <see cref="LoadModules(IReadOnlyList{string}, out List{string})"/>, but adds
    /// <paramref name="dllPaths"/>' directories to <paramref name="resolver"/> instead of a
    /// fresh one. Pass the same resolver instance across multiple calls (e.g. the user's
    /// assemblies plus a separately-loaded framework/reference set) so a type in one set can
    /// resolve a reference into the other - Cecil resolves lazily, so this works regardless of
    /// which set is loaded first, as long as both calls happen before anything calls
    /// <c>TypeReference.Resolve()</c>.
    /// </summary>
    public static List<ModuleDefinition> LoadModules(IReadOnlyList<string> dllPaths, DefaultAssemblyResolver resolver, out List<string> warnings)
    {
        warnings = [];

        var searchDirectories = new HashSet<string>();
        foreach (var dllPath in dllPaths)
        {
            var dir = Path.GetDirectoryName(dllPath);
            if (!string.IsNullOrEmpty(dir))
            {
                searchDirectories.Add(dir);
            }
        }

        foreach (var dir in searchDirectories)
        {
            resolver.AddSearchDirectory(dir);
        }

        var modules = new List<ModuleDefinition>();
        foreach (var dllPath in dllPaths)
        {
            var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            var readerParameters = new ReaderParameters
            {
                AssemblyResolver = resolver,
                ReadSymbols = File.Exists(pdbPath),
            };

            try
            {
                modules.Add(ModuleDefinition.ReadModule(dllPath, readerParameters));
            }
            catch (Exception ex)
            {
                warnings.Add($"failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }

        return modules;
    }

    /// <summary>Convenience overload for callers that don't need the DLL-load warnings.</summary>
    public static List<ModuleDefinition> LoadModules(List<string> dllPaths)
    {
        return LoadModules(dllPaths, out _);
    }
}