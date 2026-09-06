using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>
/// Exercises <see cref="FrameworkDiscovery.SelectBestVersionDirectory"/>'s version-matching
/// logic in isolation, against a fabricated directory listing - no real local .NET install
/// required (unlike <see cref="FrameworkDiscovery.TryLocateSharedFrameworkDirectory"/> itself,
/// covered end to end by <c>CliTests.Run_Implementations_WithBareFrameworkDir_ResolvesFrameworkType</c>) -
/// plus <see cref="FrameworkDiscovery.ResolveAssemblyPaths"/>'s per-entry directory-vs-file
/// classification.
/// </summary>
[TestFixture]
public class FrameworkDiscoveryTests
{
    private static readonly (string Dir, Version Version)[] Installed =
    [
        ("6.0.22", new Version(6, 0, 22)),
        ("7.0.5", new Version(7, 0, 5)),
        ("9.0.5", new Version(9, 0, 5)),
        ("9.0.9", new Version(9, 0, 9)),
        ("10.0.7", new Version(10, 0, 7)),
    ];

    [Test]
    public void PicksExactMajorMinorMatch()
    {
        var pick = FrameworkDiscovery.SelectBestVersionDirectory(Installed, new Version(9, 0));

        Assert.That(pick, Is.EqualTo("9.0.9"));
    }

    [Test]
    public void PicksHighestPatchWithinSameMajorMinor()
    {
        // Both 9.0.5 and 9.0.9 match major.minor 9.0 - the highest patch wins.
        var pick = FrameworkDiscovery.SelectBestVersionDirectory(Installed, new Version(9, 0, 0));

        Assert.That(pick, Is.EqualTo("9.0.9"));
    }

    [Test]
    public void FallsBackToHighestInstalledWithSameMajorWhenMinorMissing()
    {
        var pick = FrameworkDiscovery.SelectBestVersionDirectory(Installed, new Version(9, 5));

        Assert.That(pick, Is.EqualTo("9.0.9"));
    }

    [Test]
    public void FallsBackToHighestInstalledOverallWhenMajorMissing()
    {
        var pick = FrameworkDiscovery.SelectBestVersionDirectory(Installed, new Version(8, 0));

        Assert.That(pick, Is.EqualTo("10.0.7"));
    }

    [Test]
    public void FallsBackToHighestInstalledOverallWhenNoTargetDetected()
    {
        var pick = FrameworkDiscovery.SelectBestVersionDirectory(Installed, null);

        Assert.That(pick, Is.EqualTo("10.0.7"));
    }

    [Test]
    public void PicksTheOnlyInstalledVersionWhenThereIsExactlyOne()
    {
        var pick = FrameworkDiscovery.SelectBestVersionDirectory([("5.0.1", new Version(5, 0, 1))], new Version(9, 0));

        Assert.That(pick, Is.EqualTo("5.0.1"));
    }

    [Test]
    public void ResolveAssemblyPaths_ReturnsEmptyForAnEmptyEntryList()
    {
        Assert.That(FrameworkDiscovery.ResolveAssemblyPaths([]), Is.Empty);
    }

    [Test]
    public void ResolveAssemblyPaths_ScansADirectoryEntry_NonRecursively()
    {
        var root = CreateTempDir();
        try
        {
            var topLevel = Path.Combine(root, "TopLevel.dll");
            File.WriteAllBytes(topLevel, PeFixtures.MinimalPeHeader(managed: true));

            var subDir = Path.Combine(root, "sub");
            Directory.CreateDirectory(subDir);
            File.WriteAllBytes(Path.Combine(subDir, "Nested.dll"), PeFixtures.MinimalPeHeader(managed: true));

            var paths = FrameworkDiscovery.ResolveAssemblyPaths([root]);

            Assert.That(paths, Is.EqualTo(new[] { topLevel }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveAssemblyPaths_SkipsNativeDllsFoundViaADirectoryEntry()
    {
        var root = CreateTempDir();
        try
        {
            var managed = Path.Combine(root, "Managed.dll");
            File.WriteAllBytes(managed, PeFixtures.MinimalPeHeader(managed: true));
            File.WriteAllBytes(Path.Combine(root, "Native.dll"), PeFixtures.MinimalPeHeader(managed: false));

            var paths = FrameworkDiscovery.ResolveAssemblyPaths([root]);

            Assert.That(paths, Is.EqualTo(new[] { managed }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveAssemblyPaths_AddsAFileEntryDirectly_Unfiltered()
    {
        var root = CreateTempDir();
        try
        {
            // A native (non-managed) file passed explicitly must still come back as-is - file
            // entries bypass the managed filter a directory scan applies.
            var nativeFile = Path.Combine(root, "Native.dll");
            File.WriteAllBytes(nativeFile, PeFixtures.MinimalPeHeader(managed: false));

            var paths = FrameworkDiscovery.ResolveAssemblyPaths([nativeFile]);

            Assert.That(paths, Is.EqualTo(new[] { nativeFile }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ResolveAssemblyPaths_MixesDirectoryAndFileEntriesInOneCall()
    {
        var dirRoot = CreateTempDir();
        var looseFileRoot = CreateTempDir();
        try
        {
            var inDir = Path.Combine(dirRoot, "InDir.dll");
            File.WriteAllBytes(inDir, PeFixtures.MinimalPeHeader(managed: true));
            File.WriteAllBytes(Path.Combine(dirRoot, "Native.dll"), PeFixtures.MinimalPeHeader(managed: false));

            var looseFile = Path.Combine(looseFileRoot, "Loose.dll");
            File.WriteAllBytes(looseFile, PeFixtures.MinimalPeHeader(managed: true));

            var paths = FrameworkDiscovery.ResolveAssemblyPaths([dirRoot, looseFile]);

            Assert.That(paths, Is.EqualTo(new[] { inDir, looseFile }));
        }
        finally
        {
            Directory.Delete(dirRoot, recursive: true);
            Directory.Delete(looseFileRoot, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daq-framework-discovery-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }
}
