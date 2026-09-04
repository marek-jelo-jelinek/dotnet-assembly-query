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
    private static int Dispatch(DaemonRequest request, List<ModuleDefinition> modules, List<TypeDefinition> allTypes)
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

    private static string CompileFixture(string assemblyName, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "daq-daemon-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, assemblyName + ".dll");
        CompileFixtureInto(dllPath, assemblyName, source);
        return dllPath;
    }

    private static void CompileFixtureInto(string dllPath, string assemblyName, string source)
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
