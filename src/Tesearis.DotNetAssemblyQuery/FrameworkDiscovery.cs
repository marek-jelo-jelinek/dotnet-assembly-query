using System.Text.RegularExpressions;
using Mono.Cecil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Locates the local machine's installed <c>Microsoft.NETCore.App</c> shared-framework
/// directory that best matches an already-loaded set of modules, so BCL types (e.g.
/// <c>IDisposable</c>) can be resolved without the user manually pointing <c>--assembly</c>/
/// <c>--dir</c> at <c>System.Private.CoreLib.dll</c>. Used by a bare <c>implementations
/// --framework-dir</c>; <see cref="ResolveAssemblyPaths"/> also serves the explicit
/// directory/file-list form of that same option.
/// </summary>
public static partial class FrameworkDiscovery
{
    private const string FrameworkName = "Microsoft.NETCore.App";
    private static readonly Regex TargetFrameworkVersionRegex = TargetFrameworkVersionRegexBuilder();

    /// <summary>
    /// Attempts to find the shared-framework directory (e.g.
    /// <c>/usr/local/share/dotnet/shared/Microsoft.NETCore.App/9.0.5</c>) that best matches
    /// <paramref name="modules"/>'s target framework. Returns <c>false</c> if no shared-framework
    /// install can be found on this machine at all.
    /// </summary>
    public static bool TryLocateSharedFrameworkDirectory(IReadOnlyList<ModuleDefinition> modules, out string? directory)
    {
        directory = null;

        var frameworkRoot = LocateSharedFrameworkRoot();
        if (frameworkRoot == null) return false;

        var netCoreAppDir = Path.Combine(frameworkRoot, "shared", FrameworkName);
        if (!Directory.Exists(netCoreAppDir)) return false;

        var installed = Directory.GetDirectories(netCoreAppDir)
            .Select(dir => (Dir: dir, Version: TryParseVersion(Path.GetFileName(dir))))
            .Where(entry => entry.Version != null)
            .Select(entry => (entry.Dir, Version: entry.Version!))
            .ToList();
        if (installed.Count == 0) return false;

        directory = SelectBestVersionDirectory(installed, DetectTargetVersion(modules));
        return true;
    }

    /// <summary>
    /// Picks the best-matching entry from <paramref name="installed"/> for <paramref name="target"/>:
    /// exact major.minor match preferred, else the highest installed version with the same major,
    /// else the highest installed version overall - closest thing to "what this module set was
    /// built against" without requiring an exact patch-level install. Exposed separately from
    /// <see cref="TryLocateSharedFrameworkDirectory"/> so this selection logic can be unit-tested
    /// against a fabricated directory listing, without a real local .NET install.
    /// </summary>
    internal static string SelectBestVersionDirectory(IReadOnlyList<(string Dir, Version Version)> installed, Version? target)
    {
        if (target != null)
        {
            var exact = installed.Where(entry => entry.Version.Major == target.Major && entry.Version.Minor == target.Minor)
                .OrderByDescending(entry => entry.Version)
                .FirstOrDefault();
            if (exact.Dir != null) return exact.Dir;

            var sameMajor = installed.Where(entry => entry.Version.Major == target.Major)
                .OrderByDescending(entry => entry.Version)
                .FirstOrDefault();
            if (sameMajor.Dir != null) return sameMajor.Dir;
        }

        return installed.OrderByDescending(entry => entry.Version).First().Dir;
    }

    /// <summary>
    /// Resolves assembly paths from a mixed list of directory and file entries: a directory entry
    /// is scanned non-recursively for managed .dll files (same filtering as
    /// <see cref="AssemblyLoading.DiscoverDllPaths"/>'s --dir handling); a file entry (anything
    /// that isn't an existing directory) is added directly, unfiltered - matching that same
    /// method's --assembly convention that an explicitly-named file bypasses the managed/native
    /// filter a directory scan applies.
    /// </summary>
    public static List<string> ResolveAssemblyPaths(IReadOnlyList<string> entries)
    {
        var paths = new List<string>();
        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                foreach (var dllPath in Directory.GetFiles(entry, "*.dll"))
                {
                    if (ManagedAssemblyDetection.IsManagedAssembly(dllPath))
                    {
                        paths.Add(dllPath);
                    }
                }
            }
            else
            {
                paths.Add(entry);
            }
        }

        return paths;
    }

    private static Version? DetectTargetVersion(IReadOnlyList<ModuleDefinition> modules)
    {
        foreach (var module in modules)
        {
            foreach (var attribute in module.Assembly.CustomAttributes)
            {
                if (attribute.AttributeType.FullName != "System.Runtime.Versioning.TargetFrameworkAttribute") continue;
                if (attribute.ConstructorArguments.Count == 0) continue;

                var value = attribute.ConstructorArguments[0].Value as string;
                var match = value != null ? TargetFrameworkVersionRegex.Match(value) : Match.Empty;
                if (match.Success && Version.TryParse(match.Groups["version"].Value, out var version))
                {
                    return version;
                }
            }
        }

        // No TargetFrameworkAttribute (e.g. an older/non-SDK-style assembly) - fall back to the
        // version of the corelib it was actually built against.
        foreach (var module in modules)
        {
            foreach (var reference in module.AssemblyReferences)
            {
                if (reference.Name is "System.Private.CoreLib" or "mscorlib")
                {
                    return reference.Version;
                }
            }
        }

        return null;
    }

    private static string? LocateSharedFrameworkRoot()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot)) return dotnetRoot;

        // The running daq process's own runtime directory looks like
        // ".../dotnet/shared/Microsoft.NETCore.App/<version>/" - walk up three levels to the
        // dotnet install root.
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var ownRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir)));
        if (ownRoot != null && Directory.Exists(Path.Combine(ownRoot, "shared"))) return ownRoot;

        foreach (var candidate in WellKnownRoots())
        {
            if (Directory.Exists(Path.Combine(candidate, "shared"))) return candidate;
        }

        return null;
    }

    private static IEnumerable<string> WellKnownRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "dotnet");
        }
        else
        {
            yield return "/usr/local/share/dotnet";
            yield return "/usr/share/dotnet";
        }
    }

    private static Version? TryParseVersion(string name) => Version.TryParse(name, out var version) ? version : null;
    [GeneratedRegex(@"Version=v(?<version>\d+\.\d+)", RegexOptions.Compiled)]
    private static partial Regex TargetFrameworkVersionRegexBuilder();
}
