using Mono.Cecil;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;

namespace Tesearis.DotNetAssemblyQuery.Tests;

/// <summary>
/// Compiles a small throwaway "fixture" assembly (with a portable PDB) to a temp directory once
/// per test run, then exercises DotNetAssemblyQuery's symbol matching, location resolution, and
/// reference lookup against it.
/// </summary>
[TestFixture]
public class AssemblyQueryTests
{
    private const string FixtureSource = """
        namespace Fixture
        {
            public interface IGreeter
            {
                string Greet(string name);
            }

            public class Greeter : IGreeter
            {
                public string Prefix = "Hello, ";

                public string Name { get; set; } = "";

                public string Greet(string name)
                {
                    return Prefix + name;
                }

                public string Greet(string name, int times)
                {
                    var result = "";
                    for (var i = 0; i < times; i++)
                    {
                        result += Greet(name);
                    }

                    return result;
                }
            }

            public class Caller
            {
                public System.Collections.Generic.List<Greeter> GreeterList { get; set; } = new System.Collections.Generic.List<Greeter>();

                public Greeter[] GreeterArray = new Greeter[0];

                public string CallGreet(Greeter greeter)
                {
                    greeter.Name = "world";
                    return greeter.Name + greeter.Greet("world");
                }
            }

            public class LoudGreeter : Greeter
            {
            }

            public interface IPolite : IGreeter
            {
            }

            public class PoliteGreeter : IPolite
            {
                // Explicit interface implementation, so this doesn't also match plain-"Greet"
                // lookups the way Greeter's own Greet method does.
                string IGreeter.Greet(string name) => "Please, " + name;
            }

            public static class NativeInterop
            {
                [System.Runtime.InteropServices.DllImport("native.dll")]
                public static extern int NativeAdd(int a, int b);

                [System.Runtime.InteropServices.DllImport("native.dll", EntryPoint = "NativeSubImpl")]
                public static extern int NativeSub(int a, int b);
            }

            public enum Volume
            {
                Quiet,
                Normal,
                Loud,
            }
        }
        """;

    // A second file in the same namespace with no other type that has methods - so the
    // namespace-sibling fallback for Mood must reach into Fixture.cs, a genuinely different file.
    private const string SecondFileSource = """
        namespace Fixture
        {
            public enum Mood
            {
                Calm,
                Excited,
            }
        }
        """;

    private static string _fixtureDir = null!;
    private static string _dllPath = null!;
    private static string _secondSourcePath = null!;

    [OneTimeSetUp]
    public void CompileFixture()
    {
        _fixtureDir = Path.Combine(Path.GetTempPath(), "daq-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_fixtureDir);
        _dllPath = Path.Combine(_fixtureDir, "Fixture.dll");
        var pdbPath = Path.Combine(_fixtureDir, "Fixture.pdb");
        var sourcePath = Path.Combine(_fixtureDir, "Fixture.cs");
        File.WriteAllText(sourcePath, FixtureSource);
        _secondSourcePath = Path.Combine(_fixtureDir, "Second.cs");
        File.WriteAllText(_secondSourcePath, SecondFileSource);

        var syntaxTree = CSharpSyntaxTree.ParseText(FixtureSource, path: sourcePath, encoding: System.Text.Encoding.UTF8);
        var secondSyntaxTree = CSharpSyntaxTree.ParseText(SecondFileSource, path: _secondSourcePath, encoding: System.Text.Encoding.UTF8);
        var references = new List<MetadataReference>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
            {
                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        var compilation = CSharpCompilation.Create(
            "Fixture",
            [syntaxTree, secondSyntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var dllStream = File.Create(_dllPath);
        using var pdbStream = File.Create(pdbPath);
        var emitResult = compilation.Emit(dllStream, pdbStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        Assert.That(emitResult.Success, Is.True, () => string.Join(Environment.NewLine, emitResult.Diagnostics));
    }

    [OneTimeTearDown]
    public void CleanupFixture()
    {
        if (Directory.Exists(_fixtureDir))
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
    }

    private static (List<ModuleDefinition> Modules, List<TypeDefinition> Types) LoadFixtureModulesAndTypes()
    {
        var modules = AssemblyLoading.LoadModules(new List<string> { _dllPath });
        var types = new List<TypeDefinition>();
        foreach (var module in modules)
        {
            types.AddRange(module.GetTypes());
        }

        return (modules, types);
    }

    private static List<TypeDefinition> LoadFixtureTypes() => LoadFixtureModulesAndTypes().Types;

    /// <summary>Finds the "Greeter" overload of "Greet" with the given parameter count.</summary>
    private static MethodDefinition FindGreetOverload(List<TypeDefinition> types, int parameterCount)
    {
        foreach (var member in SymbolIndex.MatchMembers(types, "Greet"))
        {
            if (member is MethodDefinition method && method.DeclaringType.Name == "Greeter" && method.Parameters.Count == parameterCount)
            {
                return method;
            }
        }

        throw new InvalidOperationException($"No Greeter.Greet overload with {parameterCount} parameter(s) found.");
    }

    [Test]
    public void DiscoverDllPaths_DedupesSameFileReachedViaDifferentPathSpellings()
    {
        // The fixture DLL is reachable both directly via an explicit file entry and via a
        // directory-scan entry for its containing directory - these are two different path
        // spellings for the same file (relative vs. directory-scan-produced), and should
        // collapse to a single entry.
        var relativeSpelling = Path.Combine(_fixtureDir, ".", Path.GetFileName(_dllPath));

        var paths = AssemblyLoading.DiscoverDllPaths([relativeSpelling, _fixtureDir], out _);

        var matchingFixtureDll = paths.Count(p => string.Equals(Path.GetFullPath(p), _dllPath, StringComparison.OrdinalIgnoreCase));
        Assert.That(matchingFixtureDll, Is.EqualTo(1));
    }

    [Test]
    public void DiscoverDllPaths_SupportsRecursiveGlobbing()
    {
        var subDir = Path.Combine(_fixtureDir, "sub", "nested");
        Directory.CreateDirectory(subDir);
        var subDll = Path.Combine(subDir, "Nested.dll");
        File.Copy(_dllPath, subDll, overwrite: true);

        var pattern = Path.Combine(_fixtureDir, "**", "*.dll");
        var paths = AssemblyLoading.DiscoverDllPaths([pattern], out var warnings);

        Assert.That(warnings, Is.Empty);
        Assert.That(paths.Any(p => Path.GetFileName(p) == "Nested.dll"), Is.True);
        Assert.That(paths.Any(p => Path.GetFileName(p) == "Fixture.dll"), Is.True);
    }

    [Test]
    public void DiscoverDllPaths_DirScanDropsNativeDllsAndReportsACollapsedWarning()
    {
        var scanDir = Path.Combine(_fixtureDir, "dir-scan-native");
        Directory.CreateDirectory(scanDir);
        var managedPath = Path.Combine(scanDir, "Managed.dll");
        var nativePath = Path.Combine(scanDir, "Native.dll");
        File.Copy(_dllPath, managedPath, overwrite: true);
        File.WriteAllBytes(nativePath, PeFixtures.MinimalPeHeader(managed: false));

        var paths = AssemblyLoading.DiscoverDllPaths([scanDir], out var warnings);

        Assert.That(paths.Select(Path.GetFileName), Is.EquivalentTo(new[] { "Managed.dll" }));
        Assert.That(warnings, Is.EqualTo(new[] { new Warning("skipped 1 native (non-.NET) DLL(s) found via --path directory scan", VerboseOnly: true) }));
    }

    [Test]
    public void DiscoverDllPaths_DirScanWithOnlyManagedDllsReportsNoNativeWarning()
    {
        var scanDir = Path.Combine(_fixtureDir, "dir-scan-managed-only");
        Directory.CreateDirectory(scanDir);
        File.Copy(_dllPath, Path.Combine(scanDir, "Managed.dll"), overwrite: true);

        var paths = AssemblyLoading.DiscoverDllPaths([scanDir], out var warnings);

        Assert.That(paths.Select(Path.GetFileName), Is.EquivalentTo(new[] { "Managed.dll" }));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void DiscoverDllPaths_ExplicitAssemblyPathIsNotFilteredEvenIfNative()
    {
        var nativePath = Path.Combine(_fixtureDir, "ExplicitNative.dll");
        File.WriteAllBytes(nativePath, PeFixtures.MinimalPeHeader(managed: false));

        var paths = AssemblyLoading.DiscoverDllPaths([nativePath], out var warnings);

        Assert.That(paths, Is.EqualTo(new[] { nativePath }));
        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void MatchMembers_FindsType()
    {
        var types = LoadFixtureTypes();
        var matches = SymbolIndex.MatchMembers(types, "Greeter").ToList();

        var foundGreeter = false;
        foreach (var match in matches)
        {
            if (match.FullName == "Fixture.Greeter")
            {
                foundGreeter = true;
                break;
            }
        }

        Assert.That(foundGreeter, Is.True);
    }

    [Test]
    public void MatchMembers_FindsBothOverloads()
    {
        var types = LoadFixtureTypes();

        // "Greet" also matches IGreeter's interface declaration - scope to the concrete
        // Greeter class's two overloads.
        var matches = new List<MethodDefinition>();
        foreach (var member in SymbolIndex.MatchMembers(types, "Greet"))
        {
            if (member is MethodDefinition method && method.DeclaringType.Name == "Greeter")
            {
                matches.Add(method);
            }
        }

        Assert.That(matches, Has.Count.EqualTo(2));

        var parameterCounts = new List<int>();
        foreach (var match in matches)
        {
            parameterCounts.Add(match.Parameters.Count);
        }

        Assert.That(parameterCounts, Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void MatchMembers_FindsFieldAndProperty()
    {
        var types = LoadFixtureTypes();

        var foundField = false;
        foreach (var member in SymbolIndex.MatchMembers(types, "Prefix"))
        {
            if (member is FieldDefinition)
            {
                foundField = true;
                break;
            }
        }

        var foundProperty = false;
        foreach (var member in SymbolIndex.MatchMembers(types, "Name"))
        {
            if (member is PropertyDefinition)
            {
                foundProperty = true;
                break;
            }
        }

        Assert.That(foundField, Is.True);
        Assert.That(foundProperty, Is.True);
    }

    [Test]
    public void ResolveSourceLocation_ResolvesToFixtureFileAndLine()
    {
        var types = LoadFixtureTypes();
        var method = FindGreetOverload(types, parameterCount: 1);

        var location = SourceLocator.ResolveSourceLocation(method, _fixtureDir);

        Assert.That(location, Is.Not.Null);
        Assert.That(location!.Path, Does.Contain("Fixture.cs"));
    }

    [Test]
    public void ResolveSourceLocation_DoesNotTreatSiblingDirectorySharingPrefixAsAncestor()
    {
        var types = LoadFixtureTypes();
        var method = FindGreetOverload(types, parameterCount: 1);

        // A sourceRoot that shares a string prefix with the fixture directory but isn't actually its
        // ancestor (e.g. "/tmp/daq-tests-abc" vs. the real "/tmp/daq-tests-abcdef") must not be
        // treated as one - the resolved path should stay the full, unrelativized document path.
        var siblingPrefixRoot = _fixtureDir[..^1];

        var location = SourceLocator.ResolveSourceLocation(method, siblingPrefixRoot);

        Assert.That(location, Is.Not.Null);
        Assert.That(location!.Path, Is.EqualTo(Path.Combine(_fixtureDir, "Fixture.cs")));
    }

    [Test]
    public void ResolveSourceLocation_NormalizesPathSeparators()
    {
        var types = LoadFixtureTypes();
        var method = FindGreetOverload(types, parameterCount: 1);

        var invertedRoot = _fixtureDir.Contains('/') ? _fixtureDir.Replace('/', '\\') : _fixtureDir.Replace('\\', '/');

        var location = SourceLocator.ResolveSourceLocation(method, invertedRoot);

        Assert.That(location, Is.Not.Null);
        Assert.That(location!.Path, Does.EndWith("Fixture.cs"));
        Assert.That(Path.IsPathRooted(location.Path), Is.False);
    }

    [Test]
    public void ResolvesToAny_TrueForInterfaceImplementation()
    {
        var types = LoadFixtureTypes();
        var interfaceTargets = SymbolIndex.MatchMembers(types, "IGreeter").ToList();
        var interfaceTargetNames = SymbolIndex.TargetNames(interfaceTargets);
        TypeDefinition? greeterType = null;
        foreach (var type in types)
        {
            if (type.FullName == "Fixture.Greeter")
            {
                greeterType = type;
                break;
            }
        }

        Assert.That(greeterType, Is.Not.Null);

        var implementsInterface = false;
        foreach (var iface in greeterType!.Interfaces)
        {
            if (SymbolIndex.ResolvesToAny(iface.InterfaceType, interfaceTargets, interfaceTargetNames))
            {
                implementsInterface = true;
                break;
            }
        }

        Assert.That(implementsInterface, Is.True);
    }

    [Test]
    public void FindReferences_FindsCallSiteAndInterfaceImplementation()
    {
        var (modules, types) = LoadFixtureModulesAndTypes();

        var sites = AssemblyQuery.FindReferences(modules, types, "Greet", _fixtureDir);

        var foundCallSite = false;
        foreach (var site in sites)
        {
            if (!site.IsTypePositionUsage && site.Site.FullName.Contains("Fixture.Caller::CallGreet"))
            {
                foundCallSite = true;
                break;
            }
        }

        Assert.That(foundCallSite, Is.True);
    }

    [Test]
    public void FindReferences_FindsTypePositionUsages()
    {
        var (modules, types) = LoadFixtureModulesAndTypes();

        // "Greeter" is used as a parameter type on Caller.CallGreet - should be reported with
        // IsTypePositionUsage: true.
        var greeterSites = AssemblyQuery.FindReferences(modules, types, "Greeter", _fixtureDir);
        var foundParameterType = greeterSites.Any(site =>
            site.IsTypePositionUsage && site.Kind.StartsWith("parameter type") && site.Site.FullName.Contains("Fixture.Caller::CallGreet"));
        Assert.That(foundParameterType, Is.True);

        // "IGreeter" is used as an interface-position reference by Greeter's declaration.
        var interfaceSites = AssemblyQuery.FindReferences(modules, types, "IGreeter", _fixtureDir);
        var foundInterfaceImplementation = interfaceSites.Any(site =>
            site.IsTypePositionUsage && site.Kind == "interface" && site.Site.FullName == "Fixture.Greeter");
        Assert.That(foundInterfaceImplementation, Is.True);
    }

    [Test]
    public void FindReferences_FindsPropertyReadAndWrite()
    {
        var (modules, types) = LoadFixtureModulesAndTypes();

        var sites = AssemblyQuery.FindReferences(modules, types, "Name", _fixtureDir);

        // Caller.CallGreet sets and gets Greeter.Name
        var callerSites = sites.Where(s => !s.IsTypePositionUsage && s.Site.FullName.Contains("Fixture.Caller::CallGreet")).ToList();
        Assert.That(callerSites, Has.Count.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void FindReferences_FindsGenericAndArrayTypePositionUsages()
    {
        var (modules, types) = LoadFixtureModulesAndTypes();

        var sites = AssemblyQuery.FindReferences(modules, types, "Greeter", _fixtureDir);

        // Caller.GreeterList uses List<Greeter>
        var foundGenericProperty = sites.Any(s =>
            s.IsTypePositionUsage && s.Kind == "property type" && s.Site.FullName.Contains("GreeterList"));
        Assert.That(foundGenericProperty, Is.True);

        // Caller.GreeterArray uses Greeter[]
        var foundArrayField = sites.Any(s =>
            s.IsTypePositionUsage && s.Kind == "field type" && s.Site.FullName.Contains("GreeterArray"));
        Assert.That(foundArrayField, Is.True);
    }

    [Test]
    public void ResolveSourceLocation_FallsBackToApproximateLocationForFieldsAndTypes()
    {
        var types = LoadFixtureTypes();
        var field = SymbolIndex.MatchMembers(types, "Prefix").OfType<FieldDefinition>().First();

        var location = SourceLocator.ResolveSourceLocation(field, _fixtureDir);

        // Approximate locations omit the line - it would belong to an unrelated method - and report
        // just the file. Prefix's declaring type (Greeter) has its own methods, so the file is
        // trustworthy: not a namespace-sibling guess.
        Assert.That(location, Is.Not.Null);
        Assert.That(location!.IsApproximate, Is.True);
        Assert.That(location.IsFileApproximate, Is.False);
        Assert.That(location.Line, Is.Null);
        Assert.That(location.Path, Does.Contain("Fixture.cs"));
        Assert.That(location.ToString(), Is.EqualTo(location.Path));
    }

    [Test]
    public void ResolveSourceLocation_FallsBackToApproximateLocationForTypes()
    {
        var types = LoadFixtureTypes();
        var greeterType = types.Single(t => t.FullName == "Fixture.Greeter");

        var location = SourceLocator.ResolveSourceLocation(greeterType, _fixtureDir);

        Assert.That(location, Is.Not.Null);
        Assert.That(location!.IsApproximate, Is.True);
        Assert.That(location.IsFileApproximate, Is.False);
        Assert.That(location.Line, Is.Null);
        Assert.That(location.Path, Does.Contain("Fixture.cs"));
    }

    [Test]
    public void ResolveSourceLocation_FallsBackToApproximateLocationForEnums()
    {
        var types = LoadFixtureTypes();
        var volumeType = types.Single(t => t.FullName == "Fixture.Volume");

        var location = SourceLocator.ResolveSourceLocation(volumeType, _fixtureDir);

        // Volume has no methods of its own (enums never do), so this only resolves via the
        // namespace-sibling fallback - even the file is a guess here.
        Assert.That(location, Is.Not.Null);
        Assert.That(location!.IsApproximate, Is.True);
        Assert.That(location.IsFileApproximate, Is.True);
        Assert.That(location.Line, Is.Null);
        Assert.That(location.Path, Does.Contain("Fixture.cs"));
        Assert.That(location.ToString(), Is.EqualTo($"{location.Path} (approximate file)"));
    }

    [Test]
    public void ResolveSourceLocation_FallsBackToApproximateLocationForEnumMembers()
    {
        var types = LoadFixtureTypes();
        var member = SymbolIndex.MatchMembers(types, "Loud").OfType<FieldDefinition>().First();

        var location = SourceLocator.ResolveSourceLocation(member, _fixtureDir);

        Assert.That(location, Is.Not.Null);
        Assert.That(location!.IsApproximate, Is.True);
        Assert.That(location.IsFileApproximate, Is.True);
        Assert.That(location.Line, Is.Null);
        Assert.That(location.Path, Does.Contain("Fixture.cs"));
    }

    [Test]
    public void ResolveSourceLocation_NamespaceSiblingFallbackCanResolveToADifferentFileThanTheMember()
    {
        var types = LoadFixtureTypes();
        var moodType = types.Single(t => t.FullName == "Fixture.Mood");

        var location = SourceLocator.ResolveSourceLocation(moodType, _fixtureDir);

        // Mood is declared in Second.cs and has no methods anywhere in its declaring-type chain,
        // so the namespace-sibling fallback reaches into Fixture.cs instead - a real mismatch,
        // not just a hypothetical one. IsFileApproximate is the only signal that the reported
        // file may not be where Mood actually lives.
        Assert.That(location, Is.Not.Null);
        Assert.That(location!.IsFileApproximate, Is.True);
        Assert.That(location.Path, Does.Contain("Fixture.cs"));
        Assert.That(location.Path, Does.Not.Contain("Second.cs"));
    }

    [Test]
    public void ResolveSourceLocation_ResolvesAutoPropertyExactlyViaItsAccessor()
    {
        var types = LoadFixtureTypes();
        var property = SymbolIndex.MatchMembers(types, "Name").OfType<PropertyDefinition>().First();

        var location = SourceLocator.ResolveSourceLocation(property, _fixtureDir);

        Assert.That(location, Is.Not.Null);
        Assert.That(location!.IsApproximate, Is.False);
        Assert.That(location.IsFileApproximate, Is.False);
        Assert.That(location.Path, Does.Contain("Fixture.cs"));
    }

    [Test]
    public void ResolveSourceLocation_ResolvesAutoPropertyBackingFieldExactlyViaItsProperty()
    {
        var types = LoadFixtureTypes();
        var greeterType = types.Single(t => t.FullName == "Fixture.Greeter");
        var backingField = greeterType.Fields.Single(f => f.Name == "<Name>k__BackingField");

        var location = SourceLocator.ResolveSourceLocation(backingField, _fixtureDir);

        Assert.That(location, Is.Not.Null);
        Assert.That(location!.IsApproximate, Is.False);
        Assert.That(location.IsFileApproximate, Is.False);
        Assert.That(location.Path, Does.Contain("Fixture.cs"));
    }

    [Test]
    public void FindSymbol_FiltersByKind()
    {
        var types = LoadFixtureTypes();

        // "Greet" matches Greeter's two overloads plus IGreeter's interface declaration - all
        // three are methods, so the kind filter should keep all of them and reject none.
        var methodsOnly = AssemblyQuery.FindSymbol(types, "Greet", kind: "method");
        Assert.That(methodsOnly, Has.Count.EqualTo(3));
        Assert.That(methodsOnly, Has.All.InstanceOf<MethodDefinition>());

        var typesOnly = AssemblyQuery.FindSymbol(types, "Greet", kind: "type");
        Assert.That(typesOnly, Is.Empty);
    }

    [Test]
    public void FindSymbol_FiltersByNamespace()
    {
        var types = LoadFixtureTypes();

        var inFixtureNamespace = AssemblyQuery.FindSymbol(types, "Greeter", @namespace: "Fixture");
        Assert.That(inFixtureNamespace, Is.Not.Empty);

        var inOtherNamespace = AssemblyQuery.FindSymbol(types, "Greeter", @namespace: "NoSuchNamespace");
        Assert.That(inOtherNamespace, Is.Empty);
    }

    [Test]
    public void FindSymbol_FiltersByAssemblyName()
    {
        var types = LoadFixtureTypes();

        var inFixtureAssembly = AssemblyQuery.FindSymbol(types, "Greeter", assemblyName: "Fixture");
        Assert.That(inFixtureAssembly, Is.Not.Empty);

        var inOtherAssembly = AssemblyQuery.FindSymbol(types, "Greeter", assemblyName: "NoSuchAssembly");
        Assert.That(inOtherAssembly, Is.Empty);
    }

    [Test]
    public void FindSymbol_CombinesFiltersWithAndSemantics()
    {
        var types = LoadFixtureTypes();

        var allMatch = AssemblyQuery.FindSymbol(types, "Greet", kind: "method", @namespace: "Fixture", assemblyName: "Fixture");
        Assert.That(allMatch, Has.Count.EqualTo(3));

        var oneMismatches = AssemblyQuery.FindSymbol(types, "Greet", kind: "method", @namespace: "NoSuchNamespace", assemblyName: "Fixture");
        Assert.That(oneMismatches, Is.Empty);
    }

    [Test]
    public void Search_MatchesPartialSubstring()
    {
        var types = LoadFixtureTypes();

        // "oud" only occurs inside "LoudGreeter" - nothing else in the fixture contains it.
        var matches = AssemblyQuery.FindByContains(types, "oud", kind: "type");

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Name, Is.EqualTo("LoudGreeter"));
    }

    [Test]
    public void Search_IsCaseInsensitive()
    {
        var types = LoadFixtureTypes();

        var lower = AssemblyQuery.FindByContains(types, "greeter", kind: "type");
        var upper = AssemblyQuery.FindByContains(types, "GREETER", kind: "type");

        Assert.That(lower, Has.Count.EqualTo(4));
        Assert.That(upper.Select(m => m.Name), Is.EquivalentTo(lower.Select(m => m.Name)));
    }

    [Test]
    public void Search_ReturnsEmptyForNonSubstring()
    {
        var types = LoadFixtureTypes();

        var matches = AssemblyQuery.FindByContains(types, "ThisTermMatchesNothingInTheFixture");

        Assert.That(matches, Is.Empty);
    }

    [Test]
    public void Search_FiltersByKind()
    {
        var types = LoadFixtureTypes();

        // "LoudGreeter" only occurs as a type name in the fixture - no method's simple name
        // contains it (PoliteGreeter's explicit IGreeter.Greet impl is qualified "IGreeter.Greet",
        // not "LoudGreeter").
        var typesOnly = AssemblyQuery.FindByContains(types, "LoudGreeter", kind: "type");
        Assert.That(typesOnly, Has.Count.EqualTo(1));
        Assert.That(typesOnly, Has.All.InstanceOf<TypeDefinition>());

        var methodsOnly = AssemblyQuery.FindByContains(types, "LoudGreeter", kind: "method");
        Assert.That(methodsOnly, Is.Empty);
    }

    [Test]
    public void Search_FiltersByNamespace()
    {
        var types = LoadFixtureTypes();

        var inFixtureNamespace = AssemblyQuery.FindByContains(types, "Greeter", @namespace: "Fixture");
        Assert.That(inFixtureNamespace, Is.Not.Empty);

        var inOtherNamespace = AssemblyQuery.FindByContains(types, "Greeter", @namespace: "NoSuchNamespace");
        Assert.That(inOtherNamespace, Is.Empty);
    }

    [Test]
    public void Search_FiltersByAssemblyName()
    {
        var types = LoadFixtureTypes();

        var inFixtureAssembly = AssemblyQuery.FindByContains(types, "Greeter", assemblyName: "Fixture");
        Assert.That(inFixtureAssembly, Is.Not.Empty);

        var inOtherAssembly = AssemblyQuery.FindByContains(types, "Greeter", assemblyName: "NoSuchAssembly");
        Assert.That(inOtherAssembly, Is.Empty);
    }

    [Test]
    public void Search_CombinesFiltersWithAndSemantics()
    {
        var types = LoadFixtureTypes();

        var allMatch = AssemblyQuery.FindByContains(types, "Greeter", kind: "type", @namespace: "Fixture", assemblyName: "Fixture");
        Assert.That(allMatch, Has.Count.EqualTo(4));

        var oneMismatches = AssemblyQuery.FindByContains(types, "Greeter", kind: "type", @namespace: "NoSuchNamespace", assemblyName: "Fixture");
        Assert.That(oneMismatches, Is.Empty);
    }

    [Test]
    public void Hover_ReturnsFormattedSignaturesDistinctFromFindSymbol()
    {
        var types = LoadFixtureTypes();

        var symbols = AssemblyQuery.FindSymbol(types, "Prefix");
        var hovers = AssemblyQuery.Hover(types, "Prefix");

        Assert.That(hovers, Has.Count.EqualTo(symbols.Count));
        Assert.That(hovers, Has.All.Matches<string>(h => h.Contains("public") && h.Contains("Prefix")));
    }

    [Test]
    public void Hover_OnPInvokeMethod_ShowsTargetModuleAndEntryPoint()
    {
        var types = LoadFixtureTypes();

        var defaultEntryPoint = AssemblyQuery.Hover(types, "NativeAdd").Single();
        Assert.That(defaultEntryPoint, Does.Contain("// P/Invoke: native.dll!NativeAdd"));

        var explicitEntryPoint = AssemblyQuery.Hover(types, "NativeSub").Single();
        Assert.That(explicitEntryPoint, Does.Contain("// P/Invoke: native.dll!NativeSubImpl"));
    }

    [Test]
    public void Hover_OnNonPInvokeMethod_HasNoPInvokeSuffix()
    {
        var types = LoadFixtureTypes();

        var hover = AssemblyQuery.Hover(types, "Greet").First();
        Assert.That(hover, Does.Not.Contain("P/Invoke"));
    }

    [Test]
    public void Hover_DisambiguatesOverloadsByParameterList()
    {
        var types = LoadFixtureTypes();

        var oneArg = Output.Signature(FindGreetOverload(types, parameterCount: 1));
        var twoArg = Output.Signature(FindGreetOverload(types, parameterCount: 2));

        Assert.That(oneArg, Is.Not.EqualTo(twoArg));
        Assert.That(oneArg, Does.Contain("System.String name)"));
        Assert.That(twoArg, Does.Contain("System.String name, System.Int32 times)"));

        // Regression guard: the return type must appear exactly once per line, not once from
        // Output.Signature's own "{ReturnType} {QualifiedName}" and again inside QualifiedName.
        Assert.That(CountOccurrences(oneArg, "System.String"), Is.EqualTo(2)); // return type + parameter type
    }

    [Test]
    public void GoToDefinition_DisambiguatesOverloadsByParameterList()
    {
        var types = LoadFixtureTypes();

        var matches = AssemblyQuery.GoToDefinition(types, "Greet", _fixtureDir);
        var greeterMatches = matches.Where(m => m.Member is MethodDefinition method && method.DeclaringType.Name == "Greeter").ToList();

        Assert.That(greeterMatches, Has.Count.EqualTo(2));

        var qualifiedNames = greeterMatches.Select(m => Output.QualifiedName(m.Member)).ToList();
        Assert.That(qualifiedNames, Is.Unique);
        Assert.That(qualifiedNames, Has.Some.Contains("(System.String name)"));
        Assert.That(qualifiedNames, Has.Some.Contains("(System.String name, System.Int32 times)"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Test]
    public void Signature_ReportsProtectedVisibilityCorrectly()
    {
        const string source = """
            namespace Fixture2
            {
                public class Base
                {
                    protected string ProtectedField = "";
                }
            }
            """;

        var dllPath = CompileToTempDll(source, "Fixture2");
        var modules = AssemblyLoading.LoadModules(new List<string> { dllPath });
        var types = new List<TypeDefinition>();
        foreach (var module in modules)
        {
            types.AddRange(module.GetTypes());
        }

        var field = SymbolIndex.MatchMembers(types, "ProtectedField").OfType<FieldDefinition>().First();

        Assert.That(Output.Signature(field), Does.StartWith("protected "));
    }

    [Test]
    public void QualifiedName_FormatsMethodsWithParameterListAndFallsThroughForOtherMembers()
    {
        var types = LoadFixtureTypes();

        var callGreet = SymbolIndex.MatchMembers(types, "CallGreet").OfType<MethodDefinition>().First();
        Assert.That(Output.QualifiedName(callGreet), Is.EqualTo("Fixture.Caller.CallGreet(Fixture.Greeter greeter)"));

        var twoArg = FindGreetOverload(types, parameterCount: 2);
        Assert.That(Output.QualifiedName(twoArg), Is.EqualTo("Fixture.Greeter.Greet(System.String name, System.Int32 times)"));

        var field = SymbolIndex.MatchMembers(types, "Prefix").OfType<FieldDefinition>().First();
        Assert.That(Output.QualifiedName(field), Is.EqualTo(field.FullName));
    }

    [Test]
    public void ListMembers_ReturnsFieldsPropertiesAndMethods()
    {
        var types = LoadFixtureTypes();

        var members = AssemblyQuery.ListMembers(types, "Greeter");

        Assert.That(members.OfType<FieldDefinition>().Any(f => f.Name == "Prefix"), Is.True);
        Assert.That(members.OfType<PropertyDefinition>().Any(p => p.Name == "Name"), Is.True);
        Assert.That(members.OfType<MethodDefinition>().Count(m => m.Name == "Greet"), Is.EqualTo(2));
        Assert.That(members.OfType<MethodDefinition>().Any(m => m.IsConstructor), Is.True);
    }

    [Test]
    public void ListMembers_FiltersByKind()
    {
        var types = LoadFixtureTypes();

        // Also picks up the "Name" auto-property's compiler-generated backing field alongside
        // "Prefix" - both are still fields.
        var fieldsOnly = AssemblyQuery.ListMembers(types, "Greeter", kind: "field");

        Assert.That(fieldsOnly, Has.All.InstanceOf<FieldDefinition>());
        Assert.That(fieldsOnly.Any(f => f.Name == "Prefix"), Is.True);
    }

    [Test]
    public void Implementations_FindsDirectInterfaceImplementation()
    {
        var types = LoadFixtureTypes();

        var implementers = AssemblyQuery.Implementations(types, "IGreeter");

        Assert.That(implementers.Any(t => t.FullName == "Fixture.Greeter"), Is.True);
    }

    [Test]
    public void Implementations_FindsInterfaceImplementedViaBaseClass()
    {
        var types = LoadFixtureTypes();

        // LoudGreeter implements IGreeter only by inheriting Greeter - it declares no
        // "IGreeter" in its own Interfaces collection.
        var implementers = AssemblyQuery.Implementations(types, "IGreeter");

        Assert.That(implementers.Any(t => t.FullName == "Fixture.LoudGreeter"), Is.True);
    }

    [Test]
    public void Implementations_FindsImplementationOfExtendedInterface()
    {
        var types = LoadFixtureTypes();

        // PoliteGreeter implements IPolite, which extends IGreeter - it should show up as an
        // IGreeter implementer too.
        var implementers = AssemblyQuery.Implementations(types, "IGreeter");

        Assert.That(implementers.Any(t => t.FullName == "Fixture.PoliteGreeter"), Is.True);
    }

    [Test]
    public void Implementations_FindsDerivedClass()
    {
        var types = LoadFixtureTypes();

        var implementers = AssemblyQuery.Implementations(types, "Greeter");

        Assert.That(implementers.Any(t => t.FullName == "Fixture.LoudGreeter"), Is.True);
        Assert.That(implementers.Any(t => t.FullName == "Fixture.Greeter"), Is.False);
    }

    [Test]
    public void Implementations_WithNoSuchTarget_ReturnsEmpty()
    {
        var types = LoadFixtureTypes();

        var implementers = AssemblyQuery.Implementations(types, "NoSuchType");

        Assert.That(implementers, Is.Empty);
    }

    [Test]
    public void ListAssemblies_ReturnsLoadedModule()
    {
        var (modules, _) = LoadFixtureModulesAndTypes();

        var assemblies = AssemblyQuery.ListAssemblies(modules);

        var fixtureAssembly = assemblies.FirstOrDefault(a => a.Name == "Fixture");
        Assert.That(fixtureAssembly, Is.Not.Null);
        Assert.That(fixtureAssembly!.FilePath, Is.Not.Empty);
    }

    private static string CompileToTempDll(string source, string assemblyName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "daq-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var dllPath = Path.Combine(dir, assemblyName + ".dll");

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
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
        var emitResult = compilation.Emit(dllStream);
        Assert.That(emitResult.Success, Is.True, () => string.Join(Environment.NewLine, emitResult.Diagnostics));

        return dllPath;
    }
}
