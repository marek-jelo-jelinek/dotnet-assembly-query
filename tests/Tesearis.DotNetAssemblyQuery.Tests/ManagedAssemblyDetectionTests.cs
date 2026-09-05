using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

[TestFixture]
public class ManagedAssemblyDetectionTests
{
    private string _root = null!;

    [SetUp]
    public void CreateTempDir()
    {
        _root = Path.Combine(Path.GetTempPath(), "daq-managed-detect-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void CleanupTempDir()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void ReturnsTrueForAManagedPeImage()
    {
        var path = WriteFile("Managed.dll", PeFixtures.MinimalPeHeader(managed: true));

        Assert.That(ManagedAssemblyDetection.IsManagedAssembly(path), Is.True);
    }

    [Test]
    public void ReturnsFalseForANativePeImage()
    {
        var path = WriteFile("Native.dll", PeFixtures.MinimalPeHeader(managed: false));

        Assert.That(ManagedAssemblyDetection.IsManagedAssembly(path), Is.False);
    }

    [Test]
    public void ReturnsFalseForAnEmptyFile()
    {
        var path = WriteFile("Empty.dll", []);

        Assert.That(ManagedAssemblyDetection.IsManagedAssembly(path), Is.False);
    }

    [Test]
    public void ReturnsFalseForATruncatedFile()
    {
        var path = WriteFile("Truncated.dll", "MZ"u8.ToArray());

        Assert.That(ManagedAssemblyDetection.IsManagedAssembly(path), Is.False);
    }

    [Test]
    public void ReturnsFalseWhenDosHeaderIsPresentButPeSignatureIsNot()
    {
        var bytes = new byte[0x80];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        // e_lfanew at 0x3C points past the DOS header, but there's no "PE\0\0" there.
        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C);

        var path = WriteFile("NotPe.dll", bytes);

        Assert.That(ManagedAssemblyDetection.IsManagedAssembly(path), Is.False);
    }

    [Test]
    public void ReturnsFalseForANonexistentFile()
    {
        var path = Path.Combine(_root, "DoesNotExist.dll");

        Assert.That(ManagedAssemblyDetection.IsManagedAssembly(path), Is.False);
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
