using System.Reflection;
using System.Reflection.Emit;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

public sealed class AssemblyDiscoveryServiceTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"efcore-mcp-discovery-{Guid.NewGuid():N}");
    private readonly AssemblyDiscoveryService _service = new();

    public AssemblyDiscoveryServiceTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public void Discover_PrefersDebugAndReturnsOnlyProjectOutputAssemblies()
    {
        WriteProject("src/App/App.csproj", "App.Output");
        WriteDll("src/App/bin/Release/net9.0/App.Output.dll", new DateTime(2026, 1, 3));
        WriteDll("src/App/bin/Debug/net8.0/App.Output.dll", new DateTime(2026, 1, 1));
        WriteDll("src/App/bin/Debug/net8.0/Dependency.dll", new DateTime(2026, 1, 4));
        WriteDll("src/App/bin/Debug/net8.0/ref/App.Output.dll", new DateTime(2026, 1, 5));

        var candidates = _service.Discover(_workspace);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("Debug", candidates[0].Configuration);
        Assert.True(candidates[0].IsPreferred);
        Assert.Equal("Release", candidates[1].Configuration);
        Assert.False(candidates[1].IsPreferred);
        Assert.All(candidates, candidate => Assert.Equal("App.Output.dll", Path.GetFileName(candidate.AssemblyPath)));
    }

    [Fact]
    public void Discover_UsesNewestDebugOutputAcrossProjectsThenHighestTargetFramework()
    {
        WriteProject("First/First.csproj");
        WriteProject("Second/Second.csproj");
        WriteDll("First/bin/Debug/net8.0/First.dll", new DateTime(2026, 1, 1));
        WriteDll("Second/bin/Debug/net8.0/Second.dll", new DateTime(2026, 1, 2));
        WriteDll("Second/bin/Debug/net10.0/Second.dll", new DateTime(2026, 1, 2));

        var candidates = _service.Discover(_workspace);

        Assert.EndsWith(Path.Combine("Second", "bin", "Debug", "net10.0", "Second.dll"), candidates[0].AssemblyPath);
        Assert.EndsWith(Path.Combine("Second", "bin", "Debug", "net8.0", "Second.dll"), candidates[1].AssemblyPath);
    }

    [Fact]
    public void Discover_ReturnsEmptyWhenProjectsHaveNotBeenBuilt()
    {
        WriteProject("App/App.csproj");

        Assert.Empty(_service.Discover(_workspace));
    }

    [Fact]
    public void Discover_RanksAssembliesContainingDbContextBeforeOthers()
    {
        WriteProject("Tools/Tools.csproj");
        WriteProject("Data/Data.csproj");
        WriteDllWithType("Tools/bin/Debug/net8.0/Tools.dll", "Tools.Program", baseTypeName: null);
        WriteDllWithType("Data/bin/Debug/net8.0/Data.dll", "Data.MyDbContext", baseTypeName: "Microsoft.EntityFrameworkCore.DbContext");

        var candidates = _service.Discover(_workspace);

        Assert.Equal(2, candidates.Count);
        Assert.True(candidates[0].LikelyContainsDbContext);
        Assert.EndsWith("Data.dll", candidates[0].AssemblyPath);
        Assert.False(candidates[1].LikelyContainsDbContext);
        Assert.EndsWith("Tools.dll", candidates[1].AssemblyPath);
        Assert.True(candidates[0].IsPreferred);
    }

    [Fact]
    public void Discover_FlagsContextDerivingFromGenericBaseInAnotherAssembly()
    {
        WriteProject("Data/IdentityApp.csproj");
        CopyDirectory(Path.GetDirectoryName(FixturePaths.IdentityAppDllPath)!, Path.Combine(_workspace, "Data", "bin", "Debug", "net10.0"));
        WriteProject("Tools/Tools.csproj");
        WriteDllWithType("Tools/bin/Debug/net10.0/Tools.dll", "Tools.Program", baseTypeName: null);

        var candidates = _service.Discover(_workspace);

        Assert.True(candidates[0].LikelyContainsDbContext);
        Assert.EndsWith("IdentityApp.dll", candidates[0].AssemblyPath);
        Assert.False(candidates[1].LikelyContainsDbContext);
    }

    [Fact]
    public void Discover_DoesNotFlagTypeDerivingFromUnrelatedTypeNamedDbContext()
    {
        WriteProject("Fake/Fake.csproj");
        WriteDllWithLookalikeDbContext("Fake/bin/Debug/net10.0/Fake.dll");

        var candidate = Assert.Single(_service.Discover(_workspace));

        Assert.False(candidate.LikelyContainsDbContext);
    }

    [Fact]
    public void Discover_RanksTestProjectsBehindApplicationAssembliesThatContainDbContext()
    {
        WriteProject("Data.Tests/Data.Tests.csproj");
        WriteProject("Data/Data.csproj");
        WriteProject("Plain/Plain.csproj");
        WriteDllWithType("Data.Tests/bin/Debug/net8.0/Data.Tests.dll", "Data.Tests.TestDbContext", baseTypeName: "Microsoft.EntityFrameworkCore.DbContext");
        WriteDllWithType("Data/bin/Debug/net8.0/Data.dll", "Data.MyDbContext", baseTypeName: "Microsoft.EntityFrameworkCore.DbContext");
        WriteDllWithType("Plain/bin/Debug/net8.0/Plain.dll", "Plain.Program", baseTypeName: null);
        File.SetLastWriteTimeUtc(Path.Combine(_workspace, "Data.Tests", "bin", "Debug", "net8.0", "Data.Tests.dll"), new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var candidates = _service.Discover(_workspace);

        Assert.Equal(["Data.dll", "Data.Tests.dll", "Plain.dll"], candidates.Select(c => Path.GetFileName(c.AssemblyPath)));
        Assert.True(candidates[0].IsPreferred);
        Assert.True(candidates[1].IsTestProject);
        Assert.False(candidates[0].IsTestProject);
    }

    [Fact]
    public void Discover_TreatsProjectReferencingATestFrameworkAsATestProject()
    {
        WriteProjectXml("Checks/Checks.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3\" /></ItemGroup></Project>");
        WriteProject("Data/Data.csproj");
        WriteDllWithType("Checks/bin/Debug/net8.0/Checks.dll", "Checks.Fixture", baseTypeName: "Microsoft.EntityFrameworkCore.DbContext");
        WriteDllWithType("Data/bin/Debug/net8.0/Data.dll", "Data.MyDbContext", baseTypeName: "Microsoft.EntityFrameworkCore.DbContext");

        var candidates = _service.Discover(_workspace);

        Assert.EndsWith("Data.dll", candidates[0].AssemblyPath);
        Assert.True(candidates[1].IsTestProject);
    }

    [Fact]
    public void Discover_FindsArtifactsOutputLayoutWithCustomArtifactsFolder()
    {
        WriteProjectXml("src/DAL/DAL.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><AssemblyName>App.DAL</AssemblyName></PropertyGroup></Project>");
        WriteDll(".a/bin/DAL/debug/App.DAL.dll", new DateTime(2026, 1, 2));
        WriteDll(".a/bin/DAL/release/App.DAL.dll", new DateTime(2026, 1, 3));
        WriteDll(".a/bin/Other/debug/App.DAL.dll", new DateTime(2026, 1, 4));

        var candidates = _service.Discover(_workspace);

        Assert.Equal(2, candidates.Count);
        Assert.EndsWith(Path.Combine(".a", "bin", "DAL", "debug", "App.DAL.dll"), candidates[0].AssemblyPath);
        Assert.Equal("debug", candidates[0].Configuration);
        Assert.Equal("net11.0", candidates[0].TargetFramework);
        Assert.EndsWith(Path.Combine("src", "DAL", "DAL.csproj"), candidates[0].ProjectPath);
        Assert.Equal("release", candidates[1].Configuration);
    }

    [Fact]
    public void Discover_ReadsConfigurationAndTargetFrameworkFromMultiTargetArtifactsPivot()
    {
        WriteProjectXml("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks></PropertyGroup></Project>");
        WriteDll("artifacts/bin/App/debug_net10.0/App.dll", new DateTime(2026, 1, 1));
        WriteDll("artifacts/bin/App/debug_net11.0_win-x64/App.dll", new DateTime(2026, 1, 1));

        var candidates = _service.Discover(_workspace);

        Assert.Equal(["net11.0", "net10.0"], candidates.Select(candidate => candidate.TargetFramework));
        Assert.All(candidates, candidate => Assert.Equal("debug", candidate.Configuration));
    }

    [Fact]
    public void Discover_RejectsMissingWorkspace()
    {
        Directory.Delete(_workspace, recursive: true);

        var exception = Assert.Throws<AssemblyDiscoveryException>(() => _service.Discover(_workspace));

        Assert.Contains("does not exist", exception.Message);
    }

    private void WriteProject(string relativePath, string? assemblyName = null)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, assemblyName is null
            ? "<Project Sdk=\"Microsoft.NET.Sdk\" />"
            : $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>");
    }

    private void WriteProjectXml(string relativePath, string xml)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    /// <summary>Writes an assembly with a type deriving from another type of the same assembly that is merely named DbContext.</summary>
    private void WriteDllWithLookalikeDbContext(string relativePath)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(path));
        var assemblyBuilder = new PersistedAssemblyBuilder(assemblyName, typeof(object).Assembly);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);
        var lookalike = moduleBuilder.DefineType("Other.DbContext", TypeAttributes.Public | TypeAttributes.Class, typeof(object));
        lookalike.CreateType();
        moduleBuilder.DefineType("Fake.Repository", TypeAttributes.Public | TypeAttributes.Class, lookalike).CreateType();

        using var stream = File.Create(path);
        assemblyBuilder.Save(stream);
    }

    private void WriteDll(string relativePath, DateTime lastWriteTimeUtc)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc));
    }

    /// <summary>Writes a minimal, real .NET assembly (built with <see cref="PersistedAssemblyBuilder"/>)
    /// containing a single public type, optionally deriving from a base type named
    /// <paramref name="baseTypeName"/> (a <see cref="TypeReference"/>-shaped base, matching how a
    /// real DbContext subclass would reference Microsoft.EntityFrameworkCore.DbContext). This lets
    /// the "likely contains DbContext" metadata scan be exercised against a genuinely valid PE
    /// image, rather than the placeholder single-byte files <see cref="WriteDll"/> writes for tests
    /// that only care about path/ranking behavior.</summary>
    private void WriteDllWithType(string relativePath, string typeFullName, string? baseTypeName)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var lastDot = typeFullName.LastIndexOf('.');
        var ns = lastDot >= 0 ? typeFullName[..lastDot] : string.Empty;
        var typeName = lastDot >= 0 ? typeFullName[(lastDot + 1)..] : typeFullName;

        var assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(path));
        var assemblyBuilder = new PersistedAssemblyBuilder(assemblyName, typeof(object).Assembly);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName.Name!);

        Type? baseType = baseTypeName switch
        {
            "Microsoft.EntityFrameworkCore.DbContext" => typeof(Microsoft.EntityFrameworkCore.DbContext),
            null => typeof(object),
            _ => throw new NotSupportedException($"Unsupported base type '{baseTypeName}' in test helper."),
        };

        moduleBuilder.DefineType(
            string.IsNullOrEmpty(ns) ? typeName : $"{ns}.{typeName}",
            TypeAttributes.Public | TypeAttributes.Class,
            baseType).CreateType();

        using var stream = File.Create(path);
        assemblyBuilder.Save(stream);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }
}
