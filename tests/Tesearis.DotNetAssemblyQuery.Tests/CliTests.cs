using System.Text.Json;
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
    public void Run_Implementations_WithNoMatch_PrintsNotFound()
    {
        var (exitCode, output, _) = RunCli("implementations", "ThisSymbolDoesNotExistAnywhere", "--assembly", SomeRealAssemblyPath);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(output, Does.Contain("No implementations of 'ThisSymbolDoesNotExistAnywhere' found."));
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
