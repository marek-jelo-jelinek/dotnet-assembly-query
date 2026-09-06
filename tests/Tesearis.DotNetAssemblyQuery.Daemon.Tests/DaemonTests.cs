using System.IO.Pipes;
using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>Pure-function and in-process tests for the daemon's building blocks - no real OS process spawning.</summary>
[TestFixture]
public class DllSetSignatureTests
{
    [Test]
    public void Compute_IsOrderIndependent()
    {
        var a = DllSetSignature.Compute(["/a/One.dll", "/a/Two.dll"]);
        var b = DllSetSignature.Compute(["/a/Two.dll", "/a/One.dll"]);

        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void Compute_DifferentSetsProduceDifferentSignatures()
    {
        var a = DllSetSignature.Compute(["/a/One.dll"]);
        var b = DllSetSignature.Compute(["/a/One.dll", "/a/Two.dll"]);

        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void PipeName_IsDerivedFromSignature()
    {
        var signature = DllSetSignature.Compute(["/a/One.dll"]);

        Assert.That(DllSetSignature.PipeName(signature), Is.EqualTo($"daq-{signature}"));
    }
}

[TestFixture]
public class PipeFramingTests
{
    [Test]
    public void RequestRoundTripsThroughAnonymousPipe()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        var request = new DaemonRequest("find-symbol", "Foo", "/src", ["/a/One.dll", "/a/Two.dll"]);
        PipeFraming.WriteInt32(server, DaemonProtocolVersion.Current);
        PipeFraming.WriteJson(server, request, DaemonJsonContext.Default.DaemonRequest);

        var version = PipeFraming.ReadInt32(client);
        var received = PipeFraming.ReadJson(client, DaemonJsonContext.Default.DaemonRequest);

        Assert.That(version, Is.EqualTo(DaemonProtocolVersion.Current));
        Assert.That(received.Command, Is.EqualTo(request.Command));
        Assert.That(received.Name, Is.EqualTo(request.Name));
        Assert.That(received.SourceRoot, Is.EqualTo(request.SourceRoot));
        Assert.That(received.DllPaths, Is.EqualTo(request.DllPaths)); // List<T> equality: NUnit compares element-by-element
    }

    [Test]
    public void RequestRoundTripsIncludeFrameworkResults()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        var request = new DaemonRequest("implementations", "IDisposable", "/src", ["/a/One.dll"], FrameworkPaths: [], IncludeFrameworkResults: true);
        PipeFraming.WriteJson(server, request, DaemonJsonContext.Default.DaemonRequest);

        var received = PipeFraming.ReadJson(client, DaemonJsonContext.Default.DaemonRequest);

        Assert.That(received.IncludeFrameworkResults, Is.True);
    }

    [Test]
    public void ResponseRoundTripsWithMultiKilobytePayload()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        var largeStdout = string.Concat(Enumerable.Repeat("line of output\n", 2000)); // ~30KB
        var response = new DaemonResponse(0, largeStdout, "");
        PipeFraming.WriteJson(server, response, DaemonJsonContext.Default.DaemonResponse);

        var received = PipeFraming.ReadJson(client, DaemonJsonContext.Default.DaemonResponse);

        Assert.That(received.Stdout, Is.EqualTo(largeStdout));
    }
}

[TestFixture]
public class DaemonLockTests
{
    private static string UniqueSignature() => "daq-lock-test-" + Guid.NewGuid().ToString("N");

    [Test]
    public void SecondAcquireFailsWhileFirstIsHeld()
    {
        var signature = UniqueSignature();
        using var first = DaemonLock.TryAcquire(signature);
        Assert.That(first, Is.Not.Null);

        var second = DaemonLock.TryAcquire(signature);
        try
        {
            Assert.That(second, Is.Null);
        }
        finally
        {
            second?.Dispose();
            DaemonLock.Release(first!, signature);
        }
    }

    [Test]
    public void AcquireSucceedsAgainAfterRelease()
    {
        var signature = UniqueSignature();
        var first = DaemonLock.TryAcquire(signature);
        Assert.That(first, Is.Not.Null);
        DaemonLock.Release(first!, signature);

        using var second = DaemonLock.TryAcquire(signature);
        Assert.That(second, Is.Not.Null);
        DaemonLock.Release(second!, signature);
    }

    [Test]
    public void AcquireSucceedsWhenOrphanedLockFileExistsOnDisk()
    {
        var signature = UniqueSignature();
        try
        {
            // Simulate an ungraceful crash: stream is closed / process died, but .lock file remains on disk.
            var first = DaemonLock.TryAcquire(signature);
            Assert.That(first, Is.Not.Null);
            first!.Dispose(); // Close stream without deleting file

            var lockPath = Path.Combine(DaemonRegistry.RegistryDirectory, signature + ".lock");
            Assert.That(File.Exists(lockPath), Is.True);

            // A new daemon instance should be able to acquire the lock despite the existing file.
            using var second = DaemonLock.TryAcquire(signature);
            Assert.That(second, Is.Not.Null);
            DaemonLock.Release(second!, signature);
        }
        finally
        {
            DaemonRegistryTestHelpers.DeleteLockFileIfPresent(signature);
        }
    }

    [Test]
    public void ConcurrentAcquireHasExactlyOneWinner()
    {
        var signature = UniqueSignature();
        try
        {
            var results = new System.Collections.Concurrent.ConcurrentBag<FileStream?>();
            Parallel.For(0, 8, _ => results.Add(DaemonLock.TryAcquire(signature)));

            var winners = results.Where(r => r != null).ToList();
            Assert.That(winners, Has.Count.EqualTo(1));

            foreach (var winner in winners)
            {
                DaemonLock.Release(winner!, signature);
            }
        }
        finally
        {
            DaemonRegistryTestHelpers.DeleteLockFileIfPresent(signature);
        }
    }
}

/// <summary>Small helper shared by lock tests to guarantee no lock-file litter survives a failed assertion.</summary>
internal static class DaemonRegistryTestHelpers
{
    public static void DeleteLockFileIfPresent(string signature)
    {
        var path = Path.Combine(DaemonRegistry.RegistryDirectory, signature + ".lock");
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort.
        }
    }
}
