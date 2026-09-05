namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Cheaply determines whether a file is a managed (.NET) PE image by checking for a CLI
/// (COR20/COM-descriptor) header, without loading it via Mono.Cecil.
/// </summary>
internal static class ManagedAssemblyDetection
{
    /// <summary>
    /// Reads just the PE/COFF headers to check for a non-empty CLI header (data directory index
    /// 14). Returns <c>false</c> for anything unreadable, truncated, or not a PE image at all.
    /// </summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40) return false;
            if (reader.ReadUInt16() != 0x5A4D) return false; // "MZ"

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadUInt32();
            if (peOffset == 0 || peOffset + 24 > stream.Length) return false;

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt32() != 0x00004550) return false; // "PE\0\0"

            stream.Seek(2, SeekOrigin.Current); // Machine
            stream.Seek(2, SeekOrigin.Current); // NumberOfSections
            stream.Seek(12, SeekOrigin.Current); // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
            var sizeOfOptionalHeader = reader.ReadUInt16();
            stream.Seek(2, SeekOrigin.Current); // Characteristics
            if (sizeOfOptionalHeader == 0) return false;

            var optionalHeaderStart = stream.Position;
            var magic = reader.ReadUInt16();
            var comDescriptorDirOffset = magic switch
            {
                0x10b => 96, // PE32
                0x20b => 112, // PE32+
                _ => -1,
            };
            if (comDescriptorDirOffset < 0) return false;

            var dirPos = optionalHeaderStart + comDescriptorDirOffset + (14 * 8);
            if (dirPos + 8 > stream.Length || dirPos + 8 > optionalHeaderStart + sizeOfOptionalHeader) return false;

            stream.Seek(dirPos, SeekOrigin.Begin);
            var comDescriptorRva = reader.ReadUInt32();
            var comDescriptorSize = reader.ReadUInt32();
            return comDescriptorRva != 0 && comDescriptorSize != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
