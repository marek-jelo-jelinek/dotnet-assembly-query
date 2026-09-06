using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Mono.Cecil;
using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>
/// Real (but in-process, no separate OS process) round trips through <see cref="DaemonHost"/>'s
/// worker loop via the real named-pipe client - exercises the wire protocol, staleness reload,
/// and idle-timeout self-shutdown end to end. <see cref="DaemonHost.RunWorkerLoop"/> is just a
/// static method, so it's driven here on a background <see cref="Task"/> rather than requiring a
/// separately spawned process, which keeps these tests fast and non-flaky. The dispatch delegate
/// under test is a minimal stand-in for a real host's (find-symbol only, matching
/// <see cref="AssemblyQuery.FindSymbol"/>'s output shape) since these tests exercise the daemon's
/// own machinery, not any particular consumer's command set.
/// </summary>
[TestFixture]
public class DaemonIntegrationTests
{
    private static int Dispatch(DaemonRequest request, List<ModuleDefinition> modules, List<TypeDefinition> allTypes, Func<List<TypeDefinition>>? autoFrameworkTypes)
    {
        Assert.That(request.Command, Is.EqualTo("find-symbol"));
        var matches = AssemblyQuery.FindSymbol(allTypes, request.Name, request.Kind, request.Namespace, request.AssemblyName);
        if (matches.Count == 0)
        {
            Console.WriteLine($"No symbol named '{request.Name}' found.");
            return 0;
        }

        foreach (var member in matches)
        {
            Console.WriteLine(member.FullName);
        }

        return 0;
    }

    /// <summary>
    /// Minimal stand-in for <c>CliDispatch.PrintImplementations</c>: same "combine allTypes with
    /// autoFrameworkTypes() before searching" shape, so it exercises <see cref="DaemonHost"/>'s
    /// own <c>GetOrLoadFrameworkTypes</c> resolver-sharing logic the same way a real host would.
    /// </summary>
    private static int ImplementationsDispatch(DaemonRequest request, List<ModuleDefinition> modules, List<TypeDefinition> allTypes, Func<List<TypeDefinition>>? autoFrameworkTypes)
    {
        Assert.That(request.Command, Is.EqualTo("implementations"));
        if (autoFrameworkTypes != null)
        {
            allTypes = [.. allTypes, .. autoFrameworkTypes()];
        }

        var matches = AssemblyQuery.Implementations(allTypes, request.Name);
        if (matches.Count == 0)
        {
            Console.WriteLine($"No implementations of '{request.Name}' found.");
            return 0;
        }

        foreach (var type in matches)
        {
            Console.WriteLine(type.FullName);
        }

        return 0;
    }

    [Test]
    public async Task WithFrameworkDir_ResolvesAcrossSeparateDirectories_ThroughTheDaemon()
    {
        // Same shape as CliTests' equivalent, but through the real daemon path -
        // DaemonHost.GetOrLoadFrameworkTypes has its own resolver-sharing logic, distinct from
        // the CLI's one-shot Cli.RunQuery path.
        var (interfaceDir, implementerDllPath) = CompileFixtureAcrossTwoDirectories();
        var signature = DllSetSignature.Compute([implementerDllPath]);

        var hostTask = Task.Run(() => DaemonHost.RunWorkerLoop(["60", implementerDllPath], ImplementationsDispatch));
        try
        {
            await WaitUntil(() => DaemonRegistry.TryRead(signature) != null, "daemon to start");

            var request = new DaemonRequest("implementations", "IMarker", Path.GetDirectoryName(implementerDllPath)!, [implementerDllPath], FrameworkPaths: [interfaceDir]);
            var connected = DaemonClientTestHook.TryRun(request, out var exitCode, out var stdout);

            Assert.That(connected, Is.True);
            Assert.That(exitCode, Is.EqualTo(0));
            // "IMarker"/"Instance" share no substring - a silent resolution failure would print
            // "No implementations of 'IMarker' found." here, which must not satisfy this assertion.
            Assert.That(stdout, Does.Contain("Instance"));
        }
        finally
        {
            ShutDown(signature);
            await hostTask;
            CleanupFixtureDirectory(implementerDllPath);
            if (Directory.Exists(interfaceDir))
            {
                Directory.Delete(interfaceDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task WithMixedDirectoryAndFileFrameworkDir_ResolvesBoth_ThroughTheDaemon()
    {
        var (interfaceDir, looseFileDllPath, implementerDllPath) = CompileFixtureAcrossADirectoryAndALooseFile();
        var signature = DllSetSignature.Compute([implementerDllPath]);

        var hostTask = Task.Run(() => DaemonHost.RunWorkerLoop(["60", implementerDllPath], ImplementationsDispatch));
        try
        {
            await WaitUntil(() => DaemonRegistry.TryRead(signature) != null, "daemon to start");

            var request = new DaemonRequest("implementations", "IOtherMarker", Path.GetDirectoryName(implementerDllPath)!, [implementerDllPath], FrameworkPaths: [interfaceDir, looseFileDllPath]);
            var connected = DaemonClientTestHook.TryRun(request, out var exitCode, out var stdout);

            Assert.That(connected, Is.True);
            Assert.That(exitCode, Is.EqualTo(0));
            // IOtherMarker is only resolvable via the loose-file entry; the unrelated directory
            // entry is included alongside it to prove the mixed list doesn't confuse resolution.
            Assert.That(stdout, Does.Contain("Instance"));
        }
        finally
        {
            ShutDown(signature);
            await hostTask;
            CleanupFixtureDirectory(implementerDllPath);
            CleanupFixtureDirectory(looseFileDllPath);
            if (Directory.Exists(interfaceDir))
            {
                Directory.Delete(interfaceDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task WithChangedFrameworkPaths_InvalidatesCache_ButIdenticalRepeatedListReusesIt()
    {
        var (firstDir, secondDir, implementerDllPath) = CompileFixtureAcrossTwoUnrelatedFrameworkDirectories();
        var signature = DllSetSignature.Compute([implementerDllPath]);

        var hostTask = Task.Run(() => DaemonHost.RunWorkerLoop(["60", implementerDllPath], ImplementationsDispatch));
        try
        {
            await WaitUntil(() => DaemonRegistry.TryRead(signature) != null, "daemon to start");

            var firstRequest = new DaemonRequest("implementations", "IFirstMarker", Path.GetDirectoryName(implementerDllPath)!, [implementerDllPath], FrameworkPaths: [firstDir]);
            DaemonClientTestHook.TryRun(firstRequest, out var firstExitCode, out var firstStdout);
            Assert.That(firstExitCode, Is.EqualTo(0));
            Assert.That(firstStdout, Does.Contain("Instance"));

            // Changed path list (firstDir + secondDir): must reload and pick up ISecondMarker too.
            var secondRequest = new DaemonRequest("implementations", "ISecondMarker", Path.GetDirectoryName(implementerDllPath)!, [implementerDllPath], FrameworkPaths: [firstDir, secondDir]);
            DaemonClientTestHook.TryRun(secondRequest, out var secondExitCode, out var secondStdout);
            Assert.That(secondExitCode, Is.EqualTo(0));
            Assert.That(secondStdout, Does.Contain("Instance"));

            // Identical repeated list: must still resolve correctly (cache reused, not corrupted).
            var thirdRequest = new DaemonRequest("implementations", "ISecondMarker", Path.GetDirectoryName(implementerDllPath)!, [implementerDllPath], FrameworkPaths: [firstDir, secondDir]);
            var connected = DaemonClientTestHook.TryRun(thirdRequest, out var thirdExitCode, out var thirdStdout);
            Assert.That(connected, Is.True);
            Assert.That(thirdExitCode, Is.EqualTo(0));
            Assert.That(thirdStdout, Does.Contain("Instance"));
        }
        finally
        {
            ShutDown(signature);
            await hostTask;
            CleanupFixtureDirectory(implementerDllPath);
            if (Directory.Exists(firstDir))
            {
                Directory.Delete(firstDir, recursive: true);
            }

            if (Directory.Exists(secondDir))
            {
                Directory.Delete(secondDir, recursive: true);
            }
        }
    }

    [Test]
    public async Task RoundTripsAQueryAndSelfTerminatesAfterIdleTimeout()
    {
        var dllPath = CompileFixture("Fixture.RoundTrip", "public class Greeter { public string Prefix = \"Hello\"; }");
        var signature = DllSetSignature.Compute([dllPath]);

        var hostTask = Task.Run(() => DaemonHost.RunWorkerLoop(["2", dllPath], Dispatch));
        try
        {
            await WaitUntil(() => DaemonRegistry.TryRead(signature) != null, "daemon to start");

            var request = new DaemonRequest("find-symbol", "Greeter", Path.GetDirectoryName(dllPath)!, [dllPath]);
            var connected = DaemonClientTestHook.TryRun(request, out var exitCode, out var stdout);

            Assert.That(connected, Is.True);
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stdout, Does.Contain("Greeter"));

            await WaitUntil(() => DaemonRegistry.TryRead(signature) == null, "daemon to self-terminate after its idle timeout", timeoutSeconds: 10);
        }
        finally
        {
            await hostTask;
            CleanupFixtureDirectory(dllPath);
        }
    }

    [Test]
    public async Task ReloadsChangedAssemblyWhileStillWarm()
    {
        var dllPath = CompileFixture("Fixture.Reload", "public class Original { }");
        var signature = DllSetSignature.Compute([dllPath]);

        var hostTask = Task.Run(() => DaemonHost.RunWorkerLoop(["60", dllPath], Dispatch));
        try
        {
            await WaitUntil(() => DaemonRegistry.TryRead(signature) != null, "daemon to start");

            var sourceRoot = Path.GetDirectoryName(dllPath)!;
            var firstRequest = new DaemonRequest("find-symbol", "Renamed", sourceRoot, [dllPath]);
            DaemonClientTestHook.TryRun(firstRequest, out _, out var firstStdout);
            Assert.That(firstStdout, Does.Contain("No symbol named 'Renamed' found."));

            // Overwrite the same DLL+PDB paths with a recompiled fixture that now has a "Renamed" type -
            // the daemon must notice the changed mtime/length and reload before answering.
            CompileFixtureInto(dllPath, "Fixture.Reload", "public class Renamed { }");

            var secondRequest = new DaemonRequest("find-symbol", "Renamed", sourceRoot, [dllPath]);
            var connected = DaemonClientTestHook.TryRun(secondRequest, out var exitCode, out var secondStdout);

            Assert.That(connected, Is.True);
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(secondStdout, Does.Contain("Renamed"));
        }
        finally
        {
            ShutDown(signature);
            await hostTask;
            CleanupFixtureDirectory(dllPath);
        }
    }

    [Test]
    public async Task ForwardsKindFilterToDaemon()
    {
        var dllPath = CompileFixture("Fixture.KindFilter", "public class Greeter { public string Prefix = \"\"; }");
        var signature = DllSetSignature.Compute([dllPath]);

        var hostTask = Task.Run(() => DaemonHost.RunWorkerLoop(["60", dllPath], Dispatch));
        try
        {
            await WaitUntil(() => DaemonRegistry.TryRead(signature) != null, "daemon to start");

            var sourceRoot = Path.GetDirectoryName(dllPath)!;

            var fieldRequest = new DaemonRequest("find-symbol", "Prefix", sourceRoot, [dllPath], Kind: "field");
            var fieldConnected = DaemonClientTestHook.TryRun(fieldRequest, out var fieldExitCode, out var fieldStdout);
            Assert.That(fieldConnected, Is.True);
            Assert.That(fieldExitCode, Is.EqualTo(0));
            Assert.That(fieldStdout, Does.Contain("Prefix"));

            var methodRequest = new DaemonRequest("find-symbol", "Prefix", sourceRoot, [dllPath], Kind: "method");
            var methodConnected = DaemonClientTestHook.TryRun(methodRequest, out var methodExitCode, out var methodStdout);
            Assert.That(methodConnected, Is.True);
            Assert.That(methodExitCode, Is.EqualTo(0));
            Assert.That(methodStdout, Does.Contain("No symbol named 'Prefix' found."));
        }
        finally
        {
            ShutDown(signature);
            await hostTask;
            CleanupFixtureDirectory(dllPath);
        }
    }

    private static void ShutDown(string signature)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", DllSetSignature.PipeName(signature), System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.None);
            client.Connect(500);
            PipeFraming.WriteInt32(client, DaemonProtocolVersion.Current);
            PipeFraming.WriteJson(client, new DaemonRequest("__shutdown__", "", "", []), DaemonJsonContext.Default.DaemonRequest);
            PipeFraming.ReadJson(client, DaemonJsonContext.Default.DaemonResponse);
        }
        catch
        {
            // Best-effort - if this fails the hostTask await below will hang until its own idle
            // timeout, which is still bounded (60s) and will fail the test loudly rather than silently.
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string description, int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out waiting for {description}.");
    }

    /// <summary>
    /// Compiles two fixture assemblies into separate, unrelated directories: one declaring
    /// <c>IMarker</c>, the other declaring <c>Instance : IMarker</c> compiled against the first
    /// (but not copied alongside it). Names deliberately share no substring (unlike e.g.
    /// "IWidget"/"Widget"), so a test asserting on the implementer's name can't accidentally
    /// match a "not found" hint's echo of the interface's own name instead.
    /// </summary>
    private static (string InterfaceDir, string ImplementerDllPath) CompileFixtureAcrossTwoDirectories()
    {
        var interfaceDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-iface-" + Guid.NewGuid());
        Directory.CreateDirectory(interfaceDir);
        var interfaceDllPath = Path.Combine(interfaceDir, "Fixture.Interface.dll");
        CompileFixtureInto(interfaceDllPath, "Fixture.Interface", "public interface IMarker { }");

        var implementerDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-impl-" + Guid.NewGuid());
        Directory.CreateDirectory(implementerDir);
        var implementerDllPath = Path.Combine(implementerDir, "Fixture.Implementer.dll");
        CompileFixtureInto(implementerDllPath, "Fixture.Implementer", "public class Instance : IMarker { }", [interfaceDllPath]);

        return (interfaceDir, implementerDllPath);
    }

    /// <summary>
    /// Compiles three fixture assemblies: <c>IMarker</c> into its own directory (the directory-form
    /// <c>--framework-dir</c> entry), <c>IOtherMarker</c> into a standalone loose DLL in a second,
    /// unrelated directory (the file-form entry - passed by its DLL path, not its containing
    /// directory), and an implementer referencing both, declaring <c>Instance : IMarker,
    /// IOtherMarker</c>.
    /// </summary>
    private static (string InterfaceDir, string LooseFileDllPath, string ImplementerDllPath) CompileFixtureAcrossADirectoryAndALooseFile()
    {
        var interfaceDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-iface-" + Guid.NewGuid());
        Directory.CreateDirectory(interfaceDir);
        var interfaceDllPath = Path.Combine(interfaceDir, "Fixture.Interface.dll");
        CompileFixtureInto(interfaceDllPath, "Fixture.Interface", "public interface IMarker { }");

        var looseFileDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-loose-" + Guid.NewGuid());
        Directory.CreateDirectory(looseFileDir);
        var looseFileDllPath = Path.Combine(looseFileDir, "Fixture.LooseFile.dll");
        CompileFixtureInto(looseFileDllPath, "Fixture.LooseFile", "public interface IOtherMarker { }");

        var implementerDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-impl-" + Guid.NewGuid());
        Directory.CreateDirectory(implementerDir);
        var implementerDllPath = Path.Combine(implementerDir, "Fixture.Implementer.dll");
        CompileFixtureInto(implementerDllPath, "Fixture.Implementer", "public class Instance : IMarker, IOtherMarker { }", [interfaceDllPath, looseFileDllPath]);

        return (interfaceDir, looseFileDllPath, implementerDllPath);
    }

    /// <summary>
    /// Compiles two fixture assemblies (<c>IFirstMarker</c>, <c>ISecondMarker</c>) into separate,
    /// unrelated directories, and an implementer referencing both, declaring <c>Instance :
    /// IFirstMarker, ISecondMarker</c>. Used to exercise <c>GetOrLoadFrameworkTypes</c>'s cache
    /// invalidation when a request's <c>FrameworkPaths</c> list changes between calls.
    /// </summary>
    private static (string FirstDir, string SecondDir, string ImplementerDllPath) CompileFixtureAcrossTwoUnrelatedFrameworkDirectories()
    {
        var firstDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-first-" + Guid.NewGuid());
        Directory.CreateDirectory(firstDir);
        var firstDllPath = Path.Combine(firstDir, "Fixture.First.dll");
        CompileFixtureInto(firstDllPath, "Fixture.First", "public interface IFirstMarker { }");

        var secondDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-second-" + Guid.NewGuid());
        Directory.CreateDirectory(secondDir);
        var secondDllPath = Path.Combine(secondDir, "Fixture.Second.dll");
        CompileFixtureInto(secondDllPath, "Fixture.Second", "public interface ISecondMarker { }");

        var implementerDir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-impl-" + Guid.NewGuid());
        Directory.CreateDirectory(implementerDir);
        var implementerDllPath = Path.Combine(implementerDir, "Fixture.Implementer.dll");
        CompileFixtureInto(implementerDllPath, "Fixture.Implementer", "public class Instance : IFirstMarker, ISecondMarker { }", [firstDllPath, secondDllPath]);

        return (firstDir, secondDir, implementerDllPath);
    }

    private static string CompileFixture(string assemblyName, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, assemblyName + ".dll");
        CompileFixtureInto(dllPath, assemblyName, source);
        return dllPath;
    }

    private static void CompileFixtureInto(string dllPath, string assemblyName, string source, IEnumerable<string>? extraReferencePaths = null)
    {
        var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        var sourcePath = Path.Combine(Path.GetDirectoryName(dllPath)!, assemblyName + ".cs");
        File.WriteAllText(sourcePath, source);

        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: sourcePath, encoding: System.Text.Encoding.UTF8);
        var references = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        foreach (var path in extraReferencePaths ?? [])
        {
            references.Add(MetadataReference.CreateFromFile(path));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var dllStream = File.Create(dllPath);
        using var pdbStream = File.Create(pdbPath);
        var emitResult = compilation.Emit(dllStream, pdbStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        Assert.That(emitResult.Success, Is.True, () => string.Join(Environment.NewLine, emitResult.Diagnostics));
    }

    private static void CleanupFixtureDirectory(string dllPath)
    {
        var dir = Path.GetDirectoryName(dllPath);
        if (dir != null && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}

/// <summary>Exposes <see cref="DaemonClient"/>'s protocol logic with stdout captured as a string instead of written to <see cref="Console.Out"/>, for assertions.</summary>
internal static class DaemonClientTestHook
{
    public static bool TryRun(DaemonRequest request, out int exitCode, out string stdout)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var connected = DaemonClient.TryRun(request, out exitCode);
            stdout = writer.ToString();
            return connected;
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
