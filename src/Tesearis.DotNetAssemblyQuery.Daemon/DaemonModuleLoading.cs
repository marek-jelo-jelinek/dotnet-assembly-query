using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Loads modules for the daemon the same way <see cref="AssemblyLoading.LoadModules(IReadOnlyList{string}, out List{string})"/> does,
/// except the DLL and PDB bytes are read fully into memory first, so Cecil never keeps an open
/// file handle on the DLL/PDB themselves. A daemon is long-lived, so holding those handles open
/// (as reading straight from the file path would) would block a subsequent rebuild from
/// overwriting the same files while the daemon is warm - defeating the whole point of staying
/// resident. Metadata/IL is still parsed lazily from the in-memory buffer, so this doesn't cost
/// the laziness advantage that makes a warm daemon fast.
/// </summary>
internal static class DaemonModuleLoading
{
    public static List<ModuleDefinition> LoadModulesInMemory(IReadOnlyList<string> dllPaths, out List<string> warnings)
    {
        warnings = [];

        var resolver = new DefaultAssemblyResolver();
        var searchDirectories = new HashSet<string>();
        foreach (var dllPath in dllPaths)
        {
            var dir = Path.GetDirectoryName(dllPath);
            if (!string.IsNullOrEmpty(dir))
            {
                searchDirectories.Add(dir);
            }
        }

        foreach (var dir in searchDirectories)
        {
            resolver.AddSearchDirectory(dir);
        }

        var modules = new List<ModuleDefinition>();
        foreach (var dllPath in dllPaths)
        {
            try
            {
                var dllBytes = File.ReadAllBytes(dllPath);
                var dllStream = new MemoryStream(dllBytes, writable: false);

                var pdbPath = Path.ChangeExtension(dllPath, ".pdb");
                var readerParameters = new ReaderParameters { AssemblyResolver = resolver };
                if (File.Exists(pdbPath))
                {
                    readerParameters.ReadSymbols = true;
                    readerParameters.SymbolStream = new MemoryStream(File.ReadAllBytes(pdbPath), writable: false);
                    readerParameters.SymbolReaderProvider = new DefaultSymbolReaderProvider(throwIfNoSymbol: false);
                }

                modules.Add(ModuleDefinition.ReadModule(dllStream, readerParameters));
            }
            catch (Exception ex)
            {
                warnings.Add($"failed to load {Path.GetFileName(dllPath)}: {ex.Message}");
            }
        }

        return modules;
    }
}
