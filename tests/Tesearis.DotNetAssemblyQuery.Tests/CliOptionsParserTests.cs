using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>
/// Exercises <see cref="CliOptionsParser"/>'s command-line grammar directly (no filesystem access,
/// no daemon process) - complements <see cref="CliTests"/>, which drives the full pipeline through
/// <see cref="Cli.Run"/>. Only covers scenarios where a query subcommand's action would run (via the
/// captured callback) or where parsing fails outright (the daemon subcommands' own actions are never
/// exercised here, to avoid real assembly discovery/daemon side effects in a unit test).
/// </summary>
[TestFixture]
public class CliOptionsParserTests
{
    private static CliOptions? _parsedOptions;

    [SetUp]
    public void SetUp() => _parsedOptions = null;

    private static (int ExitCode, string Errors) Parse(params string[] args)
    {
        var root = CliOptionsParser.BuildRootCommand(
            options =>
            {
                _parsedOptions = options;
                return 0;
            },
            (_, _, _, _) => throw new InvalidOperationException("Not exercised: daemon subcommand actions are never run in this test file."));

        var parseResult = root.Parse(args);
        var errors = string.Join("\n", parseResult.Errors.Select(e => e.Message));

        // Mirrors the exit-code remap in Cli.Run: usage/parse errors get 2, distinct from
        // runtime failures (which stay whatever the invoked action returned).
        var invokeExitCode = parseResult.Invoke();
        var exitCode = parseResult.Errors.Count > 0 ? 2 : invokeExitCode;
        return (exitCode, errors);
    }

    [Test]
    public void ParsesCommandAndName()
    {
        var (exitCode, errors) = Parse("find-symbol", "Foo");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(errors, Is.Empty);
        Assert.That(_parsedOptions!.Command, Is.EqualTo("find-symbol"));
        Assert.That(_parsedOptions.Name, Is.EqualTo("Foo"));
        Assert.That(_parsedOptions.AssemblyPaths, Is.Empty);
        Assert.That(_parsedOptions.Directories, Is.Empty);
        Assert.That(_parsedOptions.SourceRoot, Is.EqualTo(Directory.GetCurrentDirectory()));
        Assert.That(_parsedOptions.NoDaemon, Is.False);
        Assert.That(_parsedOptions.DaemonIdleTimeoutSeconds, Is.Null);
        Assert.That(_parsedOptions.Json, Is.False);
    }

    [Test]
    public void ParsesJsonFlag()
    {
        var (exitCode, _) = Parse("find-symbol", "Foo", "--json");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.Json, Is.True);
    }

    [Test]
    public void ParsesRepeatedAssemblyAndDirFlags()
    {
        var (exitCode, _) = Parse("find-symbol", "Foo", "--assembly", "a.dll", "--assembly", "b.dll", "--dir", "d1", "--dir", "d2");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.AssemblyPaths, Is.EqualTo(new[] { "a.dll", "b.dll" }));
        Assert.That(_parsedOptions.Directories, Is.EqualTo(new[] { "d1", "d2" }));
    }

    [Test]
    public void ParsesAssemblyFlagsWithEqualsSyntax()
    {
        var (exitCode, _) = Parse("find-symbol", "Foo", "--assembly=a.dll", "--assembly=b.dll");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.AssemblyPaths, Is.EqualTo(new[] { "a.dll", "b.dll" }));
    }

    [Test]
    public void ParsesSourceRootAndNoDaemonAndIdleTimeout()
    {
        var (exitCode, _) = Parse("hover", "Foo", "--source-root", "/some/root", "--no-daemon", "--daemon-idle-timeout", "30");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.SourceRoot, Is.EqualTo("/some/root"));
        Assert.That(_parsedOptions.NoDaemon, Is.True);
        Assert.That(_parsedOptions.DaemonIdleTimeoutSeconds, Is.EqualTo(30));
    }

    [TestCase("find-symbol")]
    [TestCase("hover")]
    [TestCase("go-to-definition")]
    [TestCase("find-references")]
    public void ParsesEachQueryCommand(string commandName)
    {
        var (exitCode, _) = Parse(commandName, "Foo");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.Command, Is.EqualTo(commandName));
    }

    [Test]
    public void FailsWithNoCommand()
    {
        var (exitCode, errors) = Parse();

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain("Required command was not provided."));
        Assert.That(_parsedOptions, Is.Null);
    }

    [Test]
    public void FailsWithMissingName()
    {
        var (exitCode, errors) = Parse("find-symbol");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain("Required argument missing for command: 'find-symbol'."));
        Assert.That(_parsedOptions, Is.Null);
    }

    [Test]
    public void FailsOnUnrecognizedArgument()
    {
        var (exitCode, errors) = Parse("find-symbol", "Foo", "--bogus");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain("Unrecognized command or argument '--bogus'."));
        Assert.That(_parsedOptions, Is.Null);
    }

    [Test]
    public void FailsOnUnknownCommand()
    {
        var (exitCode, _) = Parse("bogus-command", "Foo");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(_parsedOptions, Is.Null);
    }

    [Test]
    public void FailsWhenAssemblyFlagMissingValue()
    {
        var (exitCode, errors) = Parse("find-symbol", "Foo", "--assembly");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain("Required argument missing for option: '--assembly'."));
        Assert.That(_parsedOptions, Is.Null);
    }

    [Test]
    public void DoubleDashEscapesAFlagLookingNameArgument()
    {
        // "--" is the standard way to tell an argument parser that everything after it is
        // positional, even if it looks like a flag.
        var (exitCode, errors) = Parse("find-symbol", "--", "--no-daemon");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(errors, Is.Empty);
        Assert.That(_parsedOptions!.Name, Is.EqualTo("--no-daemon"));
        Assert.That(_parsedOptions.NoDaemon, Is.False);
    }

    [TestCase("not-a-number")]
    public void FailsOnNonNumericIdleTimeout(string raw)
    {
        var (exitCode, errors) = Parse("find-symbol", "Foo", "--daemon-idle-timeout", raw);

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain("Cannot parse argument"));
        Assert.That(_parsedOptions, Is.Null);
    }

    [TestCase("0")]
    [TestCase("-5")]
    public void FailsOnNonPositiveIdleTimeout(string raw)
    {
        var (exitCode, errors) = Parse("find-symbol", "Foo", "--daemon-idle-timeout", raw);

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain($"--daemon-idle-timeout expects a positive number of seconds, got '{raw}'."));
        Assert.That(_parsedOptions, Is.Null);
    }

    [Test]
    public void ParsesKindNamespaceAndAssemblyNameFlags()
    {
        var (exitCode, _) = Parse("find-symbol", "Foo", "--kind", "method", "--namespace", "Some.Ns", "--assembly-name", "SomeAssembly");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.Kind, Is.EqualTo("method"));
        Assert.That(_parsedOptions.Namespace, Is.EqualTo("Some.Ns"));
        Assert.That(_parsedOptions.AssemblyName, Is.EqualTo("SomeAssembly"));
    }

    [Test]
    public void ParsesKindNamespaceAndAssemblyNameFlagsWithEqualsSyntax()
    {
        var (exitCode, _) = Parse("find-symbol", "Foo", "--kind=property", "--namespace=Some.Ns", "--assembly-name=SomeAssembly");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.Kind, Is.EqualTo("property"));
        Assert.That(_parsedOptions.Namespace, Is.EqualTo("Some.Ns"));
        Assert.That(_parsedOptions.AssemblyName, Is.EqualTo("SomeAssembly"));
    }

    [TestCase("type")]
    [TestCase("method")]
    [TestCase("field")]
    [TestCase("property")]
    public void AcceptsEachValidKindValue(string kind)
    {
        var (exitCode, _) = Parse("find-symbol", "Foo", "--kind", kind);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions!.Kind, Is.EqualTo(kind));
    }

    [Test]
    public void FailsOnInvalidKindValue()
    {
        var (exitCode, errors) = Parse("find-symbol", "Foo", "--kind", "bogus");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain("--kind expects one of 'type', 'method', 'field', 'property', got 'bogus'."));
        Assert.That(_parsedOptions, Is.Null);
    }

    [TestCase("--help")]
    [TestCase("-h")]
    [TestCase("-?")]
    public void RecognizesHelpFlag(string helpFlag)
    {
        var (exitCode, _) = Parse(helpFlag);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions, Is.Null);
    }

    [Test]
    public void RecognizesVersionFlag()
    {
        var (exitCode, _) = Parse("--version");

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(_parsedOptions, Is.Null);
    }

    [TestCase("--source-root", "value")]
    [TestCase("--no-daemon")]
    public void DaemonStartRejectsQueryOnlyFlags(params string[] extraArgs)
    {
        var args = new List<string> { "daemon", "start" };
        args.AddRange(extraArgs);

        var (exitCode, errors) = Parse([.. args]);

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain($"Unrecognized command or argument '{extraArgs[0]}'."));
    }

    [TestCase("--source-root", "value")]
    [TestCase("--no-daemon")]
    public void DaemonStopRejectsQueryOnlyFlags(params string[] extraArgs)
    {
        var args = new List<string> { "daemon", "stop" };
        args.AddRange(extraArgs);

        var (exitCode, errors) = Parse([.. args]);

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain($"Unrecognized command or argument '{extraArgs[0]}'."));
    }

    [Test]
    public void DaemonStartRejectsUnrecognizedFlag()
    {
        var (exitCode, errors) = Parse("daemon", "start", "--bogus");

        Assert.That(exitCode, Is.EqualTo(2));
        Assert.That(errors, Does.Contain("Unrecognized command or argument '--bogus'."));
    }
}
