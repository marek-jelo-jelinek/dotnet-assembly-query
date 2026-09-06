namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>
/// Builds the minimal PE32 byte layout <see cref="ManagedAssemblyDetection"/> parses, so tests
/// can exercise it (and the --path directory-scan filter built on it) without shipping real binary fixtures.
/// </summary>
internal static class PeFixtures
{
    private const int PeOffset = 0x40;
    private const int OptionalHeaderSize = 224; // magic + 16 PE32 data directories
    private const int ComDescriptorDirOffset = 96 + (14 * 8);

    public static byte[] MinimalPeHeader(bool managed)
    {
        var totalSize = PeOffset + 4 + 20 + OptionalHeaderSize;
        var bytes = new byte[totalSize];

        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(PeOffset).CopyTo(bytes, 0x3C);

        bytes[PeOffset] = (byte)'P';
        bytes[PeOffset + 1] = (byte)'E';
        bytes[PeOffset + 2] = 0;
        bytes[PeOffset + 3] = 0;

        var coffOffset = PeOffset + 4;
        BitConverter.GetBytes((ushort)OptionalHeaderSize).CopyTo(bytes, coffOffset + 16); // SizeOfOptionalHeader

        var optionalHeaderOffset = coffOffset + 20;
        BitConverter.GetBytes((ushort)0x10b).CopyTo(bytes, optionalHeaderOffset); // PE32 magic

        if (managed)
        {
            var dirOffset = optionalHeaderOffset + ComDescriptorDirOffset;
            BitConverter.GetBytes((uint)0x2000).CopyTo(bytes, dirOffset); // RVA
            BitConverter.GetBytes((uint)0x48).CopyTo(bytes, dirOffset + 4); // Size
        }

        return bytes;
    }
}
