using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>Resolves DLL paths from CLI arguments and loads them as Mono.Cecil modules.</summary>
public static class AssemblyLoading
{
    /// <summary>
    /// Resolves DLL paths from <c>--assembly</c>/<c>--dir</c>, falling back to the current
    /// directory. Unresolved patterns are reported in <paramref name="warnings"/>.
    /// </summary>
    public static List<string> DiscoverDllPaths(IReadOnlyList<string> assemblyPaths, IReadOnlyList<string> directories, out List<string> warnings)
    {
        var paths = new List<string>();
        warnings = [];

        foreach (var pattern in assemblyPaths)
        {
            var matches = ResolveGlob(pattern);
            if (matches.Count == 0)
            {
                warnings.Add($"--assembly matched no files: {pattern}");
            }

            paths.AddRange(matches);
        }

        var effectiveDirectories = new List<string>(directories);
        if (directories.Count == 0 && assemblyPaths.Count == 0)
        {
            effectiveDirectories.Add(Directory.GetCurrentDirectory());
        }

        foreach (var dir in effectiveDirectories)
        {
            if (!Directory.Exists(dir))
            {
                warnings.Add($"--dir not found: {dir}");
                continue;
            }

            try
            {
                paths.AddRange(Directory.GetFiles(dir, "*.dll"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                warnings.Add($"--dir could not be read: {dir} ({ex.Message})");
            }
        }

        // Normalize before dedup so the same DLL reached via two different path spellings (e.g.
        // via --assembly vs. a --dir scan) is only loaded once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniquePaths = new List<string>();
        foreach (var path in paths)
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
    /// Loads each DLL as a Mono.Cecil module, using its PDB when present. Failed loads are
    /// reported in <paramref name="warnings"/>. Callers must dispose the returned modules.
    /// </summary>
    public static List<ModuleDefinition> LoadModules(IReadOnlyList<string> dllPaths, out List<string> warnings)
    {
        warnings = [];

        var resolver = new DefaultAssemblyResolver();
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