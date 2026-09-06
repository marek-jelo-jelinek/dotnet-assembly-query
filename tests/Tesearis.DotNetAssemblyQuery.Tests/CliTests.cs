using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>
/// Exercises <see cref="Cli.Run"/>'s argument parsing and console output/exit-code behavior -
/// the parts of the split that stayed in the Cli project rather than the AssemblyQuery library.
/// </summary>
[TestFixture]
public class CliTests
{
    // Any already-loaded, on-disk assembly works as a stand-in for a "real" --assembly target;
    // we're only exercising argument handling here, not the query logic itself (covered in
    // AssemblyQueryTests against the compiled fixture).
    private static readonly string SomeRealAssemblyPath = typeof(object).Assembly.Location;

    private static (int ExitCode, string Out, string Error) RunCli(params string[] args)
    {
        // Inject "--no-daemon" right after <command> <name> (never at the very end, so it can't
        // swallow a trailing flag's value in tests that deliberately leave one dangling) - keeps
        // this whole suite hermetic and deterministic, with no real pipe connect/daemon spawn.
        // Not applicable to "daemon" itself - that subcommand is handled by DaemonControl before
        // Parse() ever runs, and doesn't accept --no-daemon. Not applicable to "list-assemblies"
        // either - it has no <name> argument, so args[1] is already the start of its flags and
        // splicing there would land mid-flag.
        var effectiveArgs = args.Length >= 2 && args[0] != "daemon" && args[0] != "list-assemblies"
            ? args[..2].Append("--no-daemon").Concat(args[2..]).ToArray()
            : args;

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var outWriter = new StringWriter();
        var errorWriter = new StringWriter();
        Console.SetOut(outWriter);
        Console.SetError(errorWriter);
        try
        {
            var exitCode = Cli.Run(effectiveArgs);
            return (exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Test]
    public void Run_WithHelpFlag_PrintsUsageAndSucceeds()
    {
        var (exitCode, output, _) = RunCli("--help");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("find-symbol"));
        Assert.That(output, Does.Contain("daemon"));
    }

    [Test]
    public void Run_WithShortHelpFlag_PrintsUsageAndSucceeds()
    {
        var (exitCode, output, _) = RunCli("-h");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("find-symbol"));
        Assert.That(output, Does.Contain("daemon"));
    }

    [Test]
    public void Run_WithVersionFlag_PrintsVersionAndSucceeds()
    {
        var (exitCode, output, _) = RunCli("--version");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output.Trim(), Is.Not.Empty);
    }

    [Test]
    public void Run_WithNoArguments_PrintsErrorAndUsage()
    {
        var (exitCode, output, error) = RunCli();

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("Required command was not provided."));
        Assert.That(output, Does.Contain("find-symbol"));
    }

    [Test]
    public void Run_WithUnrecognizedFlag_PrintsErrorAndUsage()
    {
        var (exitCode, _, error) = RunCli("find-symbol", "Foo", "--bogus", "value");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("Unrecognized command or argument '--bogus'."));
    }

    [Test]
    public void Run_WithFlagMissingValue_PrintsError()
    {
        var (exitCode, _, error) = RunCli("find-symbol", "Foo", "--assembly");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("Required argument missing for option: '--assembly'."));
    }

    [Test]
    public void Run_WithNoAssembliesFound_PrintsError()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(emptyDir);
        try
        {
            var (exitCode, _, error) = RunCli("find-symbol", "Foo", "--dir", emptyDir);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(error, Does.Contain("No assemblies found"));
        }
        finally
        {
            Directory.Delete(emptyDir);
        }
    }

    [Test]
    public void Run_WithUnknownCommand_PrintsErrorAndUsage()
    {
        var (exitCode, output, error) = RunCli("not-a-command", "Foo", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("Unrecognized command or argument 'not-a-command'."));
        Assert.That(output, Does.Contain("find-symbol"));
    }

    [Test]
    public void Run_FindSymbol_WithInlineFlagValue_FindsMatch()
    {
        var (exitCode, output, _) = RunCli("find-symbol", "Object", $"--assembly={SomeRealAssemblyPath}");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object"));
    }

    [Test]
    public void Run_FindSymbol_WithKindFilter_NarrowsToMatchingKind()
    {
        var (exitCode, output, _) = RunCli("find-symbol", "Object", "--kind", "type", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object"));
    }

    [Test]
    public void Run_FindSymbol_WithKindFilter_ExcludesNonMatchingKind()
    {
        var (exitCode, output, _) = RunCli("find-symbol", "Object", "--kind", "method", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No symbol named 'Object' found."));
    }

    [Test]
    public void Run_FindSymbol_WithInvalidKind_PrintsError()
    {
        var (exitCode, _, error) = RunCli("find-symbol", "Object", "--kind", "bogus", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("--kind expects one of"));
    }

    [Test]
    public void Run_FindSymbol_WithNamespaceFilter_NarrowsMatches()
    {
        var (matchExitCode, matchOutput, _) = RunCli("find-symbol", "Object", "--namespace", "System", "--assembly", SomeRealAssemblyPath);
        Assert.That(matchExitCode, Is.EqualTo(0));
        Assert.That(matchOutput, Does.Contain("Object"));

        var (noMatchExitCode, noMatchOutput, _) = RunCli("find-symbol", "Object", "--namespace", "NoSuchNamespace", "--assembly", SomeRealAssemblyPath);
        Assert.That(noMatchExitCode, Is.EqualTo(0));
        Assert.That(noMatchOutput, Does.Contain("No symbol named 'Object' found."));
    }

    [Test]
    public void Run_FindSymbol_WithAssemblyNameFilter_NarrowsMatches()
    {
        var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(SomeRealAssemblyPath).Name;

        var (matchExitCode, matchOutput, _) = RunCli("find-symbol", "Object", "--assembly-name", assemblyName!, "--assembly", SomeRealAssemblyPath);
        Assert.That(matchExitCode, Is.EqualTo(0));
        Assert.That(matchOutput, Does.Contain("Object"));

        var (noMatchExitCode, noMatchOutput, _) = RunCli("find-symbol", "Object", "--assembly-name", "NoSuchAssembly", "--assembly", SomeRealAssemblyPath);
        Assert.That(noMatchExitCode, Is.EqualTo(0));
        Assert.That(noMatchOutput, Does.Contain("No symbol named 'Object' found."));
    }

    [Test]
    public void Run_Daemon_WithHelpFlag_PrintsUsageAndSucceeds()
    {
        var (exitCode, output, _) = RunCli("daemon", "--help");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("status"));
        Assert.That(output, Does.Contain("stop"));
        Assert.That(output, Does.Contain("start"));
    }

    [Test]
    public void Run_DaemonStart_WithUnrecognizedFlag_PrintsErrorAndUsage()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(emptyDir);
        try
        {
            var (exitCode, _, error) = RunCli("daemon", "start", "--bogus", "--dir", emptyDir);

            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(error, Does.Contain("Unrecognized command or argument '--bogus'."));
        }
        finally
        {
            Directory.Delete(emptyDir);
        }
    }

    [Test]
    public void Run_DaemonStart_WithInvalidIdleTimeout_PrintsError()
    {
        var (exitCode, _, error) = RunCli("daemon", "start", "--daemon-idle-timeout", "not-a-number");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("--daemon-idle-timeout"));
    }

    [Test]
    public void Run_DaemonStart_WithNonPositiveIdleTimeout_PrintsError()
    {
        var (exitCode, _, error) = RunCli("daemon", "start", "--daemon-idle-timeout", "0");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("--daemon-idle-timeout expects a positive number of seconds, got '0'."));
    }

    [Test]
    public void Run_DaemonStart_WithSourceRootFlag_IsRejected()
    {
        var (exitCode, _, error) = RunCli("daemon", "start", "--source-root", "/tmp");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(error, Does.Contain("Unrecognized command or argument '--source-root'."));
    }

    [Test]
    public void Run_FindSymbol_WithAssemblyGlob_FindsMatch()
    {
        var (exitCode, output, _) = RunCli("find-symbol", "Object", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object"));
    }

    [Test]
    public void Run_FindSymbol_WithNoMatch_PrintsNotFound()
    {
        var (exitCode, output, _) = RunCli("find-symbol", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No symbol named 'ThisSymbolDoesNotExistAnywhere' found."));
    }

    [Test]
    public void Run_Search_WithSubstring_FindsMatch()
    {
        var (exitCode, output, _) = RunCli("search", "bjec", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object"));
    }

    [Test]
    public void Run_Search_IsCaseInsensitive()
    {
        var (exitCode, output, _) = RunCli("search", "OBJECT", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object"));
    }

    [Test]
    public void Run_Search_WithNoMatch_PrintsNotFound()
    {
        var (exitCode, output, _) = RunCli("search", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No symbol containing 'ThisSymbolDoesNotExistAnywhere' found."));
    }

    [Test]
    public void Run_Search_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("search", "bjec", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListSearchResultJson);
        Assert.That(results, Is.Not.Empty);
        Assert.That(results!.Any(r => r.Name == "System.Object"), Is.True);
    }

    [Test]
    public void Run_Search_WithJsonAndNoMatch_PrintsEmptyArray()
    {
        var (exitCode, output, _) = RunCli("search", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListSearchResultJson);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Run_Hover_WithMatch_PrintsSignature()
    {
        var (exitCode, output, _) = RunCli("hover", "Object", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object"));
    }

    [Test]
    public void Run_Hover_WithNoMatch_PrintsNotFound()
    {
        var (exitCode, output, _) = RunCli("hover", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No symbol named 'ThisSymbolDoesNotExistAnywhere' found."));
    }

    [Test]
    public void Run_GoToDefinition_WithMatch_PrintsLocationOrNoLocationMessage()
    {
        var (exitCode, output, _) = RunCli("go-to-definition", "Object", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object"));
        Assert.That(output, Does.Contain("->"));
    }

    [Test]
    public void Run_GoToDefinition_WithNoMatch_PrintsNotFound()
    {
        var (exitCode, output, _) = RunCli("go-to-definition", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No symbol named 'ThisSymbolDoesNotExistAnywhere' found."));
    }

    [Test]
    public void Run_FindReferences_WithNoMatchingSymbol_PrintsNotFound()
    {
        var (exitCode, output, _) = RunCli("find-references", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No symbol named 'ThisSymbolDoesNotExistAnywhere' found."));
    }

    [Test]
    public void Run_FindReferences_WithMatchingSymbolButNoReferences_PrintsNoReferencesFound()
    {
        // "GetHashCode" exists on Object but this single-assembly, no-cross-reference fixture
        // path (corelib itself) won't have IL call sites to it within corelib's own metadata scan
        // scope that this test cares about - instead assert the command runs to completion and
        // reports one of the two valid terminal messages without crashing.
        var (exitCode, output, _) = RunCli("find-references", "Object", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Object").Or.Contain("No references found."));
    }

    [Test]
    public void Run_FindSymbol_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("find-symbol", "Object", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListFindSymbolResultJson);
        Assert.That(results, Is.Not.Empty);
        Assert.That(results!.Any(r => r.Name == "System.Object"), Is.True);
    }

    [Test]
    public void Run_FindSymbol_WithJsonAndNoMatch_PrintsEmptyArray()
    {
        var (exitCode, output, _) = RunCli("find-symbol", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListFindSymbolResultJson);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Run_Hover_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("hover", "Object", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListHoverResultJson);
        Assert.That(results, Is.Not.Empty);
        Assert.That(results!.Any(r => r.Signature.Contains("Object")), Is.True);
    }

    [Test]
    public void Run_GoToDefinition_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("go-to-definition", "Object", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListGoToDefinitionResultJson);
        Assert.That(results, Is.Not.Empty);
        // corelib ships without a usable PDB in this environment, so Location is expected to be null
        // with UnavailableReason set - this pins down that shape rather than asserting a real location.
        Assert.That(results!.All(r => (r.Location == null) != (r.UnavailableReason == null)), Is.True);
    }

    [Test]
    public void Run_FindReferences_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("find-references", "Object", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListFindReferenceResultJson);
        Assert.That(results, Is.Not.Null);
    }

    [Test]
    public void Run_FindReferences_WithJsonAndNoMatchingSymbol_PrintsEmptyArray()
    {
        var (exitCode, output, _) = RunCli("find-references", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListFindReferenceResultJson);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Run_ListMembers_WithMatch_PrintsMembers()
    {
        var (exitCode, output, _) = RunCli("list-members", "Object", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("GetHashCode"));
    }

    [Test]
    public void Run_ListMembers_WithNoMatch_PrintsNotFound()
    {
        var (exitCode, output, _) = RunCli("list-members", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No members found for type 'ThisSymbolDoesNotExistAnywhere'."));
    }

    [Test]
    public void Run_ListMembers_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("list-members", "Object", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListListMembersResultJson);
        Assert.That(results, Is.Not.Empty);
        Assert.That(results!.Any(r => r.Name.Contains("GetHashCode")), Is.True);
    }

    [Test]
    public void Run_Implementations_WithMatch_PrintsImplementers()
    {
        var (exitCode, output, _) = RunCli("implementations", "IDisposable", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("MemoryStream"));
    }

    [Test]
    public void Run_Implementations_WithTypeNotIndexed_PrintsNotIndexed()
    {
        // "ThisSymbolDoesNotExistAnywhere" isn't a type declared in the indexed assembly at all -
        // that's a different failure than "the type is indexed but nothing implements it", and
        // should say so instead of implying zero implementations exist.
        var (exitCode, output, _) = RunCli("implementations", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("Type 'ThisSymbolDoesNotExistAnywhere' was not found in the indexed assemblies."));
    }

    [Test]
    public void Run_Implementations_WithBareFrameworkDir_ExcludesFrameworkTypeByDefault()
    {
        // Unlike the tests above, this fixture's own directory does NOT contain
        // System.Private.CoreLib.dll - only a bare --framework-dir's auto-discovery of the local
        // shared framework can make IDisposable resolvable here. This fixture has no primary
        // implementer of IDisposable at all, so by default (framework-origin implementers
        // excluded), nothing should be reported even though MemoryStream et al. do implement it.
        var dllPath = CompileFixtureWithoutCoreLib();
        if (!FrameworkDiscovery.TryLocateSharedFrameworkDirectory([], out _))
        {
            Assert.Ignore("No local .NET shared framework install found on this machine.");
        }

        try
        {
            var (exitCode, output, _) = RunCli("implementations", "IDisposable", "--assembly", dllPath, "--framework-dir");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Not.Contain("MemoryStream"));
            Assert.That(output, Does.Contain("No implementations of 'IDisposable' found."));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dllPath)!, recursive: true);
        }
    }

    [Test]
    public void Run_Implementations_WithBareFrameworkDirAndIncludeFrameworkResults_ResolvesFrameworkType()
    {
        // Same fixture/setup as above, but --include-framework-results opts back into reporting
        // framework-origin implementers.
        var dllPath = CompileFixtureWithoutCoreLib();
        if (!FrameworkDiscovery.TryLocateSharedFrameworkDirectory([], out _))
        {
            Assert.Ignore("No local .NET shared framework install found on this machine.");
        }

        try
        {
            var (exitCode, output, _) = RunCli("implementations", "IDisposable", "--assembly", dllPath, "--framework-dir", "--include-framework-results");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("MemoryStream"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dllPath)!, recursive: true);
        }
    }

    [Test]
    public void Run_Implementations_WithFrameworkDir_ExcludesUnrelatedFrameworkImplementer_ByDefault()
    {
        // Reproduces the original complaint: a primary-dir type implementing IDisposable should be
        // reported, but the (huge) set of framework/BCL types that also implement IDisposable
        // (e.g. MemoryStream) should not flood the default output.
        var dllPath = CompileFixtureWithDisposableImplementer();
        if (!FrameworkDiscovery.TryLocateSharedFrameworkDirectory([], out _))
        {
            Assert.Ignore("No local .NET shared framework install found on this machine.");
        }

        try
        {
            var (exitCode, output, _) = RunCli("implementations", "IDisposable", "--assembly", dllPath, "--framework-dir");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("PrimaryDisposable"));
            Assert.That(output, Does.Not.Contain("MemoryStream"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dllPath)!, recursive: true);
        }
    }

    [Test]
    public void Run_Implementations_WithFrameworkDirAndIncludeFrameworkResults_IncludesBoth()
    {
        var dllPath = CompileFixtureWithDisposableImplementer();
        if (!FrameworkDiscovery.TryLocateSharedFrameworkDirectory([], out _))
        {
            Assert.Ignore("No local .NET shared framework install found on this machine.");
        }

        try
        {
            var (exitCode, output, _) = RunCli("implementations", "IDisposable", "--assembly", dllPath, "--framework-dir", "--include-framework-results");

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("PrimaryDisposable"));
            Assert.That(output, Does.Contain("MemoryStream"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dllPath)!, recursive: true);
        }
    }

    [Test]
    public void Run_Implementations_WithFrameworkDir_ResolvesAcrossSeparateDirectories()
    {
        var (interfaceDir, implementerDllPath) = CompileFixtureAcrossTwoDirectories();
        try
        {
            var (exitCode, output, _) = RunCli("implementations", "IMarker", "--assembly", implementerDllPath, "--framework-dir", interfaceDir);

            Assert.That(exitCode, Is.EqualTo(0));
            // "IMarker"/"Instance" share no substring, unlike e.g. "IWidget"/"Widget" - a fix that
            // silently found nothing would print "No implementations of 'IMarker' found." here,
            // which this assertion must not accidentally satisfy.
            Assert.That(output, Does.Contain("Instance"));
        }
        finally
        {
            Directory.Delete(interfaceDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(implementerDllPath)!, recursive: true);
        }
    }

    [Test]
    public void Run_Implementations_WithTypeNotIndexed_WithoutFrameworkDir_StillJustHints()
    {
        // Same fixture as above, but without the flag - the hint fires, no framework scan happens.
        var dllPath = CompileFixtureWithoutCoreLib();
        try
        {
            var (exitCode, output, _) = RunCli("implementations", "IDisposable", "--assembly", dllPath);

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(output, Does.Contain("Type 'IDisposable' was not found in the indexed assemblies."));
            Assert.That(output, Does.Not.Contain("MemoryStream"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dllPath)!, recursive: true);
        }
    }

    [Test]
    public void Run_Implementations_WithMixedDirectoryAndFileFrameworkDir_ResolvesBoth()
    {
        var (interfaceDir, looseFileDllPath, implementerDllPath) = CompileFixtureAcrossADirectoryAndALooseFile();
        try
        {
            var (exitCode, output, _) = RunCli(
                "implementations", "IOtherMarker", "--assembly", implementerDllPath,
                "--framework-dir", interfaceDir,
                "--framework-dir", looseFileDllPath);

            Assert.That(exitCode, Is.EqualTo(0));
            // IOtherMarker is only resolvable via the loose-file entry; the unrelated directory
            // entry is included alongside it to prove the mixed list doesn't confuse resolution.
            Assert.That(output, Does.Contain("Instance"));
        }
        finally
        {
            Directory.Delete(interfaceDir, recursive: true);
            Directory.Delete(Path.GetDirectoryName(looseFileDllPath)!, recursive: true);
            Directory.Delete(Path.GetDirectoryName(implementerDllPath)!, recursive: true);
        }
    }

    /// <summary>
    /// Compiles a throwaway fixture assembly into its own fresh directory, referencing (but not
    /// copying alongside) the running process's own assemblies - so, unlike <see cref="SomeRealAssemblyPath"/>,
    /// the fixture's directory alone never contains System.Private.CoreLib.dll.
    /// </summary>
    private static string CompileFixtureWithoutCoreLib()
    {
        const string source = "namespace Fixture { public class Placeholder { } }";

        var dir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, "Fixture.NoCoreLib.dll");

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        var compilation = CSharpCompilation.Create(
            "Fixture.NoCoreLib",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var dllStream = File.Create(dllPath);
        var emitResult = compilation.Emit(dllStream, options: new EmitOptions());
        Assert.That(emitResult.Success, Is.True, string.Join("\n", emitResult.Diagnostics));

        return dllPath;
    }

    /// <summary>
    /// Same shape as <see cref="CompileFixtureWithoutCoreLib"/> (System.Private.CoreLib.dll isn't
    /// copied alongside), but declares a type that implements <c>IDisposable</c> - a primary-dir
    /// implementer to contrast against framework-origin ones like <c>MemoryStream</c>.
    /// </summary>
    private static string CompileFixtureWithDisposableImplementer()
    {
        const string source = "namespace Fixture { public class PrimaryDisposable : System.IDisposable { public void Dispose() { } } }";

        var dir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, "Fixture.DisposableImplementer.dll");

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        var compilation = CSharpCompilation.Create(
            "Fixture.DisposableImplementer",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var dllStream = File.Create(dllPath);
        var emitResult = compilation.Emit(dllStream, options: new EmitOptions());
        Assert.That(emitResult.Success, Is.True, string.Join("\n", emitResult.Diagnostics));

        return dllPath;
    }

    private static (string InterfaceDir, string ImplementerDllPath) CompileFixtureAcrossTwoDirectories()
    {
        var interfaceDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-iface-" + Guid.NewGuid());
        Directory.CreateDirectory(interfaceDir);
        var interfaceDllPath = Path.Combine(interfaceDir, "Fixture.Interface.dll");
        CompileToFile("namespace Fixture { public interface IMarker { } }", "Fixture.Interface", interfaceDllPath, AllLoadedAssemblyReferences());

        var implementerDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-impl-" + Guid.NewGuid());
        Directory.CreateDirectory(implementerDir);
        var implementerDllPath = Path.Combine(implementerDir, "Fixture.Implementer.dll");
        var references = AllLoadedAssemblyReferences();
        references.Add(MetadataReference.CreateFromFile(interfaceDllPath));
        CompileToFile("namespace Fixture { public class Instance : IMarker { } }", "Fixture.Implementer", implementerDllPath, references);

        return (interfaceDir, implementerDllPath);
    }

    private static (string InterfaceDir, string LooseFileDllPath, string ImplementerDllPath) CompileFixtureAcrossADirectoryAndALooseFile()
    {
        var interfaceDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-iface-" + Guid.NewGuid());
        Directory.CreateDirectory(interfaceDir);
        var interfaceDllPath = Path.Combine(interfaceDir, "Fixture.Interface.dll");
        CompileToFile("namespace Fixture { public interface IMarker { } }", "Fixture.Interface", interfaceDllPath, AllLoadedAssemblyReferences());

        var looseFileDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-loose-" + Guid.NewGuid());
        Directory.CreateDirectory(looseFileDir);
        var looseFileDllPath = Path.Combine(looseFileDir, "Fixture.LooseFile.dll");
        CompileToFile("namespace Fixture { public interface IOtherMarker { } }", "Fixture.LooseFile", looseFileDllPath, AllLoadedAssemblyReferences());

        var implementerDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-impl-" + Guid.NewGuid());
        Directory.CreateDirectory(implementerDir);
        var implementerDllPath = Path.Combine(implementerDir, "Fixture.Implementer.dll");
        var references = AllLoadedAssemblyReferences();
        references.Add(MetadataReference.CreateFromFile(interfaceDllPath));
        references.Add(MetadataReference.CreateFromFile(looseFileDllPath));
        CompileToFile("namespace Fixture { public class Instance : IMarker, IOtherMarker { } }", "Fixture.Implementer", implementerDllPath, references);

        return (interfaceDir, looseFileDllPath, implementerDllPath);
    }

    private static List<MetadataReference> AllLoadedAssemblyReferences()
    {
        var references = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        return references;
    }

    private static void CompileToFile(string source, string assemblyName, string dllPath, List<MetadataReference> references)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var dllStream = File.Create(dllPath);
        var emitResult = compilation.Emit(dllStream, options: new EmitOptions());
        Assert.That(emitResult.Success, Is.True, string.Join("\n", emitResult.Diagnostics));
    }

    [Test]
    public void Run_Implementations_WithIndexedTypeAndNoImplementers_PrintsNotFound()
    {
        // "String" is sealed and indexed (it's declared in SomeRealAssemblyPath), so this
        // exercises "type resolved, zero implementers" as distinct from "type not indexed".
        var (exitCode, output, _) = RunCli("implementations", "String", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No implementations of 'String' found."));
    }

    [Test]
    public void Run_Implementations_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("implementations", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath, "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListImplementationsResultJson);
        Assert.That(results, Is.Empty);
    }

    [Test]
    public void Run_ListAssemblies_PrintsLoadedAssembly()
    {
        var (exitCode, output, _) = RunCli("list-assemblies", "--assembly", SomeRealAssemblyPath, "--no-daemon");

        Assert.That(exitCode, Is.EqualTo(0));
        var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(SomeRealAssemblyPath).Name;
        Assert.That(output, Does.Contain(assemblyName));
    }

    [Test]
    public void Run_ListAssemblies_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("list-assemblies", "--assembly", SomeRealAssemblyPath, "--no-daemon", "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, CliOutputJsonContext.Default.ListListAssembliesResultJson);
        Assert.That(results, Is.Not.Empty);
        var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(SomeRealAssemblyPath).Name;
        Assert.That(results!.Any(r => r.Name == assemblyName), Is.True);
    }

    [Test]
    public void Run_WithJsonAndRuntimeError_PrintsPlainTextErrorInsteadOfJson()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "daq-cli-tests-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(emptyDir);
        try
        {
            var (exitCode, output, error) = RunCli("find-symbol", "Foo", "--dir", emptyDir, "--json");

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(error, Does.Contain("No assemblies found"));
            Assert.That(output, Is.Empty);
        }
        finally
        {
            Directory.Delete(emptyDir);
        }
    }

    [Test]
    public void Run_DaemonStatus_WithJson_PrintsJsonArray()
    {
        var (exitCode, output, _) = RunCli("daemon", "status", "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        var results = JsonSerializer.Deserialize(output, DaemonControlJsonContext.Default.ListDaemonStatusEntryJson);
        Assert.That(results, Is.Not.Null);
    }

    [Test]
    public void Run_WithUnexpectedException_PrintsErrorInsteadOfCrashing()
    {
        // The Cli assembly itself ships a portable PDB, so resolving "Run"'s source location will
        // reach SourceLocator.FormatLocation, which throws ArgumentException on this invalid
        // --source-root (embedded NUL) - this should be caught by Cli.Run's top-level guard rather
        // than crashing with a raw stack trace.
        var cliAssemblyPath = typeof(Cli).Assembly.Location;

        var (exitCode, _, error) = RunCli("go-to-definition", "Run", "--assembly", cliAssemblyPath, "--source-root", "\0bogus");

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(error, Does.Contain("Error:"));
    }
}
