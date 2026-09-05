using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>
/// Exercises <see cref="FrameworkDiscovery.SelectBestVersionDirectory"/>'s version-matching
/// logic in isolation, against a fabricated directory listing - no real local .NET install
/// required (unlike <see cref="FrameworkDiscovery.TryLocateSharedFrameworkDirectory"/> itself,
/// covered end to end by <c>CliTests.Run_Implementations_WithAutoFramework_ResolvesFrameworkType</c>).
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
}
